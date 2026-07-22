namespace JobTrack.Web.EndToEndTests;

using Abstractions;
using Application;
using AwesomeAssertions;
using Database;
using ExternalApiClient;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using NodaTime;
using Npgsql;
using Persistence.PostgreSql;
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     First-party client proof (plan §4.5, ADR 0029/0030): drives the real web host, on both
///     providers, using only <c>JobTrack.ExternalApiClient</c> -- a project with no reference to any
///     <c>JobTrack.*</c> library assembly -- authenticating with a personal access token and exercising
///     a read workflow, a mutation workflow, conflict handling, and revocation handling.
/// </summary>
public sealed class ExternalApiClientProofTests
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	[Fact]
	public async Task The_client_proof_passes_against_the_sqlite_host()
	{
		await using var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();
		await DeploySchemaAsync(database, SchemaProvider.Sqlite);

		using var factory = new TestWebApplicationFactory(SchemaProvider.Sqlite, database.ConnectionString);
		await RunProofAsync(() => JobTrackSqlite.Create(database.ConnectionString), factory, database, SchemaProvider.Sqlite);
	}

	[Fact]
	public async Task The_client_proof_passes_against_the_postgresql_host()
	{
		await using var database = new PostgreSqlDatabaseFixture();
		await database.InitializeAsync();
		await DeploySchemaAsync(database, SchemaProvider.PostgreSql);

		using var factory = new TestWebApplicationFactory(SchemaProvider.PostgreSql, database.ConnectionString);
		await RunProofAsync(
			() => JobTrackPostgreSql.Create(new NpgsqlDataSourceBuilder(database.ConnectionString).UseNodaTime().Build()),
			factory, database, SchemaProvider.PostgreSql);
	}

	private static async Task RunProofAsync(
		Func<IJobTrackClient> createSeedClient, TestWebApplicationFactory factory, IDisposableTestDatabase database, SchemaProvider provider)
	{
		var seedClient = createSeedClient();
		var bootstrap = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.client-proof",
			Password = "Bootstrap-Horse-Battery-77!",
			CorrelationId = Guid.NewGuid(),
		});
		var workerId = await SeedWorkerAsync(seedClient, bootstrap.AdministratorId);
		var leaf = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = bootstrap.AdministratorId, CorrelationId = Guid.NewGuid() },
			ParentId = bootstrap.RootJobNodeId,
			Description = "Fit cabinets",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() { Actor = bootstrap.AdministratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
		});
		await IdentityTestSupport.ClearRequiresPasswordChangeAsync(provider, database.ConnectionString);
		var issued = await seedClient.Tokens.IssueAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			TargetUserId = workerId,
			Label = "external-api-client-proof",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});

		using var httpClient = factory.CreateClient();
		using var apiClient = new JobTrackApiClient(httpClient, issued.Token);

		// Read workflow.
		var detail = await apiClient.GetJobNodeAsync(leaf.Id.Value);
		detail.Node.Description.Should().Be("Fit cabinets");
		detail.Ancestors.Should().Contain(ancestor => ancestor.Id == bootstrap.RootJobNodeId.Value);

		// Subtree read workflow (ADR 0039/0040): the worker owns the leaf directly, so the cost
		// roll-up is included without any cost-viewing role.
		var subtree = await apiClient.GetJobSubtreeAsync(leaf.Id.Value);
		subtree.RootId.Should().Be(leaf.Id.Value);
		subtree.Nodes.Should().ContainSingle(node => node.Id == leaf.Id.Value);
		subtree.RootTotal.Should().NotBeNull();

		// Mutation workflow.
		var session = await apiClient.StartSessionAsync(leaf.Id.Value, workerId.Value);
		session.LeafWorkId.Should().Be(leaf.Id.Value);

		// Conflict handling: retrying the same start collides with the invariant, not a crash.
		var conflict = await Record.ExceptionAsync(() => apiClient.StartSessionAsync(leaf.Id.Value, workerId.Value));
		conflict.Should().BeOfType<JobTrackApiConflictException>();

		// Composite mutation workflow (ADR 0045): atomically finish the confirmed active session and
		// record Success, over the plain-JSON external client -- no JobTrack.* library reference. The
		// achievement advance to InProgress uses the library-side seed client purely as test setup
		// (StartSessionAsync, unlike StartWorkAsync, does not itself auto-advance achievement).
		var leafWork = await seedClient.Query.GetLeafWorkAsync(
			new() { Context = new() { Actor = bootstrap.AdministratorId, CorrelationId = Guid.NewGuid() }, JobNodeId = leaf.Id });
		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = new() { Actor = bootstrap.AdministratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
			NewAchievement = Achievement.InProgress,
			Reason = "Prepare client-proof completion fixture",
			Version = leafWork.Version,
		});
		var completed = await apiClient.CompleteLeafAsync(
			leaf.Id.Value, leafWork.Version + 1, [new() { Id = session.Id, Version = session.Version }]);
		completed.Achievement.Should().Be("Success");
		completed.FinishedSessions.Should().ContainSingle(finished => finished.Id == session.Id);

		// Revocation handling: once the token is revoked, the next call fails cleanly, not a crash.
		await seedClient.Tokens.RevokeAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			TargetUserId = workerId,
			TokenId = issued.Id,
		});
		var revoked = await Record.ExceptionAsync(() => apiClient.GetJobNodeAsync(leaf.Id.Value));
		revoked.Should().BeOfType<JobTrackApiUnauthorizedException>();

		// Requester intake workflow (ADR 0033, plan §9 Stage 7): submit and read back a request over
		// the external API, using the same client proof, no JobTrack.* library reference.
		var holdingAreaId = await SeedHoldingAreaAsync(database, provider, bootstrap.RootJobNodeId, bootstrap.AdministratorId);
		var requesterId = await SeedRequesterAsync(seedClient, bootstrap.AdministratorId);
		await IdentityTestSupport.ClearRequiresPasswordChangeAsync(provider, database.ConnectionString);
		var requesterToken = await seedClient.Tokens.IssueAsync(new() {
			Context = new() { Actor = requesterId, CorrelationId = Guid.NewGuid() },
			TargetUserId = requesterId,
			Label = "external-api-client-proof-requester",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});

		using var requesterHttpClient = factory.CreateClient();
		using var requesterApiClient = new JobTrackApiClient(requesterHttpClient, requesterToken.Token);

		var eligibleHoldingAreas = await requesterApiClient.GetEligibleHoldingAreasAsync();
		eligibleHoldingAreas.Should().Contain(holdingArea => holdingArea.Id == holdingAreaId);

		var submitted = await requesterApiClient.SubmitRequestAsync("Printer will not turn on", holdingAreaId);
		submitted.Description.Should().Be("Printer will not turn on");

		var myRequests = await requesterApiClient.GetMyRequestsAsync();
		myRequests.Should().ContainSingle(request => request.JobNodeId == submitted.JobNodeId);

		// Request detail, acknowledgement, and notes (ADR 0034, plan §9 Stage 9): the requester reads
		// their own detail and adds a note; an administrator (always passes CanManage regardless of
		// ownership) acknowledges the request and adds a staff note, all over the same external
		// client, no JobTrack.* library reference.
		var requesterDetail = await requesterApiClient.GetRequestDetailAsync(submitted.JobNodeId);
		requesterDetail.Status.Should().Be("Submitted");
		requesterDetail.Subtree.Should().ContainSingle(node => node.JobNodeId == submitted.JobNodeId);

		var requesterNote = await requesterApiClient.AddRequestNoteAsync(submitted.JobNodeId, "Any update?", false);
		requesterNote.VisibleToRequester.Should().BeTrue("a requester-authored note is always visible to the requester");

		var adminToken = await seedClient.Tokens.IssueAsync(new() {
			Context = new() { Actor = bootstrap.AdministratorId, CorrelationId = Guid.NewGuid() },
			TargetUserId = bootstrap.AdministratorId,
			Label = "external-api-client-proof-admin",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});
		using var adminHttpClient = factory.CreateClient();
		using var adminApiClient = new JobTrackApiClient(adminHttpClient, adminToken.Token);

		var acknowledged = await adminApiClient.AcknowledgeRequestAsync(submitted.JobNodeId, submitted.Version);
		acknowledged.AcknowledgedAt.Should().NotBeNull();

		var staffNote = await adminApiClient.AddRequestNoteAsync(submitted.JobNodeId, "Triage: assigning now", false);
		staffNote.VisibleToRequester.Should().BeFalse();

		var staffDetail = await adminApiClient.GetRequestDetailAsync(submitted.JobNodeId);
		staffDetail.Status.Should().Be("Accepted");
		staffDetail.Notes.Should().HaveCount(2, "staff see both the requester's note and their own private note");

		var requesterFinalDetail = await requesterApiClient.GetRequestDetailAsync(submitted.JobNodeId);
		requesterFinalDetail.Notes.Should().ContainSingle("the requester does not see the staff-private note");
	}

	private static async Task<AppUserId> SeedRequesterAsync(IJobTrackClient seedClient, AppUserId administratorId)
	{
		var result = await seedClient.Employees.CreateEmployeeAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			DisplayName = "Client Proof Requester",
			IanaTimeZone = "Etc/UTC",
			UserName = "requester.client-proof",
			Password = "Requester-Horse-Battery-99!",
			Role = EmployeeRole.Requester,
		});

		return result.Id;
	}

	private static async Task<long> SeedHoldingAreaAsync(
		IDisposableTestDatabase database, SchemaProvider provider, JobNodeId rootId, AppUserId ownerId)
	{
		const short priorityMedium = 2;

		switch (provider) {
			case SchemaProvider.Sqlite: {
					await using var connection = new SqliteConnection(database.ConnectionString);
					await connection.OpenAsync();

					await using var insertNode = connection.CreateCommand();
					insertNode.CommandText = """
										 INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
										 VALUES ($parentId, 'Holding area', $ownerId, $ownerId, $priorityId, $postedAt);
										 SELECT last_insert_rowid();
										 """;
					_ = insertNode.Parameters.AddWithValue("$parentId", rootId.Value);
					_ = insertNode.Parameters.AddWithValue("$ownerId", ownerId.Value);
					_ = insertNode.Parameters.AddWithValue("$priorityId", priorityMedium);
					_ = insertNode.Parameters.AddWithValue("$postedAt", DateTimeOffset.UtcNow.UtcTicks - DateTime.UnixEpoch.Ticks);
					var jobNodeId = (long)(await insertNode.ExecuteScalarAsync())!;

					await using var insertHoldingArea = connection.CreateCommand();
					insertHoldingArea.CommandText = """
												INSERT INTO request_holding_area (job_node_id, name, default_priority_id, is_active)
												VALUES ($jobNodeId, 'IT Intake', $priorityId, 1);
												SELECT last_insert_rowid();
												""";
					_ = insertHoldingArea.Parameters.AddWithValue("$jobNodeId", jobNodeId);
					_ = insertHoldingArea.Parameters.AddWithValue("$priorityId", priorityMedium);
					return (long)(await insertHoldingArea.ExecuteScalarAsync())!;
				}
			case SchemaProvider.PostgreSql: {
					await using var connection = new NpgsqlConnection(database.ConnectionString);
					await connection.OpenAsync();

					await using var insertNode = connection.CreateCommand();
					insertNode.CommandText = """
										 INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
										 VALUES (@parentId, 'Holding area', @ownerId, @ownerId, @priorityId, now())
										 RETURNING id;
										 """;
					insertNode.Parameters.AddWithValue("parentId", rootId.Value);
					insertNode.Parameters.AddWithValue("ownerId", ownerId.Value);
					insertNode.Parameters.AddWithValue("priorityId", priorityMedium);
					var jobNodeId = (long)(await insertNode.ExecuteScalarAsync())!;

					await using var insertHoldingArea = connection.CreateCommand();
					insertHoldingArea.CommandText = """
												INSERT INTO request_holding_area (job_node_id, name, default_priority_id, is_active)
												VALUES (@jobNodeId, 'IT Intake', @priorityId, true)
												RETURNING id;
												""";
					insertHoldingArea.Parameters.AddWithValue("jobNodeId", jobNodeId);
					insertHoldingArea.Parameters.AddWithValue("priorityId", priorityMedium);
					return (long)(await insertHoldingArea.ExecuteScalarAsync())!;
				}
			default:
				throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported provider.");
		}
	}

	private static async Task<AppUserId> SeedWorkerAsync(IJobTrackClient seedClient, AppUserId administratorId)
	{
		var result = await seedClient.Employees.CreateEmployeeAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			DisplayName = "Client Proof Worker",
			IanaTimeZone = "Etc/UTC",
			UserName = "worker.client-proof",
			Password = "Worker-Horse-Battery-99!",
			Role = EmployeeRole.Worker,
		});

		return result.Id;
	}

	private static async Task DeploySchemaAsync(IDisposableTestDatabase database, SchemaProvider provider)
	{
		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(provider));

		switch (provider) {
			case SchemaProvider.Sqlite:
				await using (var connection = new SqliteConnection(database.ConnectionString)) {
					await connection.OpenAsync();
					await using (var pragma = connection.CreateCommand()) {
						pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
						_ = await pragma.ExecuteNonQueryAsync();
					}

					var deployer = new SchemaDeployer(connection, new SqliteSchemaVersionStore(), new SqliteDeploymentLockStrategy(),
						ApplicationVersion, AppliedBy);
					await deployer.DeployAsync(scripts, CancellationToken.None);
				}

				break;
			case SchemaProvider.PostgreSql:
				await using (var connection = new NpgsqlConnection(database.ConnectionString)) {
					await connection.OpenAsync();
					var deployer = new SchemaDeployer(connection, new PostgreSqlSchemaVersionStore(), new PostgreSqlDeploymentLockStrategy(),
						ApplicationVersion, AppliedBy);
					await deployer.DeployAsync(scripts, CancellationToken.None);
				}

				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported provider.");
		}
	}

	private sealed class TestWebApplicationFactory(SchemaProvider provider, string connectionString) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", provider.ToString());
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", connectionString);
		}
	}
}
