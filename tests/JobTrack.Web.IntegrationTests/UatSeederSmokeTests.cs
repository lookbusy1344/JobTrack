namespace JobTrack.Web.IntegrationTests;

using Abstractions;
using Application;
using AwesomeAssertions;
using Database;
using Microsoft.Data.Sqlite;
using NodaTime;
using Persistence.Sqlite;
using TestSupport;
using UatSeed;

/// <summary>
///     Proves the end-user-testing readiness synthetic seed (remediation plan §2.3) applies cleanly to a
///     freshly deployed, freshly bootstrapped database, matching how a UAT operator would run it after
///     README.md's "Running on a development server" steps. Uses SQLite since it needs no separate
///     server; the PostgreSQL path shares the same <see cref="UatSeeder.SeedAsync" /> and
///     <see cref="IJobTrackClient" /> contract, so PostgreSQL-specific coverage would be duplication, not
///     additional evidence.
/// </summary>
public sealed class UatSeederSmokeTests : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const int ExpectedRequesterDemoRequestCount = 6;

	private const string ConfigureSqliteConnectionSql =
		"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";

	private readonly SqliteDatabaseFixture database = new();

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task The_seed_applies_to_a_freshly_bootstrapped_database_and_produces_the_expected_scenario()
	{
		await DeploySchemaAsync();
		var client = JobTrackSqlite.Create(database.ConnectionString);
		var bootstrap = await client.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.uat-seed-smoke",
			Password = "Bootstrap-Horse-Battery-77!",
			CorrelationId = Guid.NewGuid(),
		});

		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using (var pragma = connection.CreateCommand()) {
			pragma.CommandText = ConfigureSqliteConnectionSql;
			_ = await pragma.ExecuteNonQueryAsync();
		}

		var summary = await UatSeeder.SeedAsync(client, connection, bootstrap.AdministratorId);

		var context = new CommandContext { Actor = bootstrap.AdministratorId, CorrelationId = Guid.NewGuid() };
		var unassignedRequest = await client.Requests.GetDetailAsync(new() { Context = context, NodeId = summary.UnassignedRequestNodeId });
		unassignedRequest.AcknowledgedAt.Should().BeNull();

		var assignedRequest = await client.Query.GetJobNodeAsync(new() { Context = context, NodeId = summary.AssignedRequestNodeId });
		assignedRequest.Node.OwnerUserId.Should().Be(summary.WorkerId);

		var poolLeaf = await client.Query.GetJobNodeAsync(new() { Context = context, NodeId = summary.PoolLeafNodeId });
		poolLeaf.Node.OwnerUserId.Should().BeNull();

		var readiness = await client.Query.GetReadinessAsync(new() { Context = context, NodeId = summary.BlockedLeafNodeId });
		readiness.IsReady.Should().BeFalse();

		var workerContext = new CommandContext { Actor = summary.WorkerId, CorrelationId = Guid.NewGuid() };
		var activeSessions = await client.Query.GetActiveSessionsAsync(new() {
			Context = workerContext,
			LeafWorkIds = [summary.ActiveSessionLeafNodeId],
		});
		activeSessions.Should().ContainSingle();

		var costDetail = await client.Costs.GetCostDetailsAsync(new() {
			Context = context,
			NodeId = summary.CostReportableLeafNodeId,
			AsOf = SystemClock.Instance.GetCurrentInstant(),
		});
		costDetail.Trace.Should().NotBeEmpty();
		costDetail.DisplayedCost.Amount.Should().BePositive();
	}

	[Fact]
	public async Task The_requester_demo_seed_creates_six_requests_spanning_open_and_closed_states()
	{
		await DeploySchemaAsync();
		var client = JobTrackSqlite.Create(database.ConnectionString);
		var bootstrap = await client.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.requester-demo",
			Password = "Bootstrap-Horse-Battery-77!",
			CorrelationId = Guid.NewGuid(),
		});
		var adminContext = new CommandContext { Actor = bootstrap.AdministratorId, CorrelationId = Guid.NewGuid() };
		var jobManager = await client.Employees.CreateEmployeeAsync(new() {
			Context = adminContext,
			DisplayName = "Demo Worker",
			IanaTimeZone = "Europe/London",
			UserName = "demo.requester-demo",
			Password = "demo1234",
			Role = EmployeeRole.JobManager,
		});
		_ = await client.Employees.AssignRoleAsync(new() {
			Context = adminContext with { CorrelationId = Guid.NewGuid() },
			TargetUserId = jobManager.Id,
			Role = EmployeeRole.Worker,
		});
		var requester = await client.Employees.CreateEmployeeAsync(new() {
			Context = adminContext with { CorrelationId = Guid.NewGuid() },
			DisplayName = "Client Requester",
			IanaTimeZone = "Europe/London",
			UserName = "requester",
			Password = "requester1234",
			Role = EmployeeRole.Requester,
		});

		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using (var pragma = connection.CreateCommand()) {
			pragma.CommandText = ConfigureSqliteConnectionSql;
			_ = await pragma.ExecuteNonQueryAsync();
		}

		var summary = await UatSeeder.SeedRequesterDemoAsync(client, connection, jobManager.Id, requester.Id);

		summary.RequestNodeIds.Should().HaveCount(ExpectedRequesterDemoRequestCount);
		var requesterContext = new CommandContext { Actor = requester.Id, CorrelationId = Guid.NewGuid() };
		var requests = await client.Requests.GetMyRequestsAsync(requesterContext);
		requests.Should().HaveCount(ExpectedRequesterDemoRequestCount);
		var statuses = new List<RequesterStatus>();
		foreach (var nodeId in summary.RequestNodeIds) {
			var detail = await client.Requests.GetDetailAsync(new() {
				Context = requesterContext with { CorrelationId = Guid.NewGuid() },
				NodeId = nodeId,
			});
			detail.RequesterUserId.Should().Be(requester.Id);
			var node = await client.Query.GetJobNodeAsync(new() { Context = adminContext with { CorrelationId = Guid.NewGuid() }, NodeId = nodeId });
			node.Node.OwnerUserId.Should().Be(jobManager.Id);
			statuses.Add(detail.Status);
		}

		statuses.Should().BeEquivalentTo([
			RequesterStatus.Submitted,
			RequesterStatus.Accepted,
			RequesterStatus.Waiting,
			RequesterStatus.InProgress,
			RequesterStatus.Completed,
			RequesterStatus.Cancelled,
		]);
	}

	private async Task DeploySchemaAsync()
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using (var pragma = connection.CreateCommand()) {
			pragma.CommandText = ConfigureSqliteConnectionSql;
			_ = await pragma.ExecuteNonQueryAsync();
		}

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.Sqlite));
		var deployer = new SchemaDeployer(connection, new SqliteSchemaVersionStore(), new SqliteDeploymentLockStrategy(), ApplicationVersion,
			AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}
}
