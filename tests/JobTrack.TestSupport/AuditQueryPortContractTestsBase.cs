namespace JobTrack.TestSupport;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using NodaTime;

/// <summary>
///     Shared contract for <see cref="IAuditQueryPort" /> (impl plan §7.4 step 3, §7.3 slice 11: query
///     audit history using sensitive-field projections), asserted identically against PostgreSQL and
///     SQLite by one thin sealed subclass per provider's own test project -- same shape as
///     <see cref="CostQueryPortContractTestsBase" />. Exercises the real port through
///     <see
///         cref="AuditQueries" />
///     (not called directly), mirroring <c>AuditQueriesTests</c>' scenarios
///     against the fake port. No command port in this library yet writes <c>audit_event</c> rows (a
///     pre-existing gap across every prior persistence slice, out of scope here), so this base seeds
///     rows directly with parameterized SQL -- legitimate because the table is insert-only, not
///     insert-and-update-forbidden.
/// </summary>
public abstract class AuditQueryPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected AuditQueryPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	private static Instant At(int hour) => Instant.FromUtc(2026, 1, 1, hour, 0);

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	[Fact]
	public async Task An_auditor_without_cost_visibility_sees_a_rate_events_metadata_but_not_its_payload()
	{
		var (auditorId, _, _) = await SeedEventsAsync();
		var sut = new AuditQueries(CreateAuditQueryPort(database.ConnectionString));

		var results = await sut.SearchAuditEventsAsync(new() { Context = ContextFor(auditorId), Filter = new() { EntityType = "user_cost_rate" } });

		results.Events.Should().ContainSingle();
		results.Events[0].EntityType.Should().Be("user_cost_rate");
		results.Events[0].Operation.Should().Be("add-user-cost-rate");
		results.Events[0].IsRedacted.Should().BeTrue();
		results.Events[0].AfterData.Should().BeNull();
	}

	[Fact]
	public async Task An_auditor_with_cost_visibility_sees_a_rate_events_full_payload()
	{
		var (_, costViewerAuditorId, _) = await SeedEventsAsync();
		var sut = new AuditQueries(CreateAuditQueryPort(database.ConnectionString));

		var results = await sut.SearchAuditEventsAsync(new() {
			Context = ContextFor(costViewerAuditorId),
			Filter = new() { EntityType = "user_cost_rate" },
		});

		results.Events.Should().ContainSingle();
		results.Events[0].IsRedacted.Should().BeFalse();
		results.Events[0].AfterData!.Value.Should().ContainKey("amount_per_hour");
	}

	[Fact]
	public async Task A_non_sensitive_event_is_never_redacted_even_without_cost_visibility()
	{
		var (auditorId, _, _) = await SeedEventsAsync();
		var sut = new AuditQueries(CreateAuditQueryPort(database.ConnectionString));

		var results = await sut.SearchAuditEventsAsync(new() { Context = ContextFor(auditorId), Filter = new() { EntityType = "job_node" } });

		results.Events.Should().ContainSingle();
		results.Events[0].IsRedacted.Should().BeFalse();
		results.Events[0].AfterData!.Value.Should().ContainKey("description");
	}

	[Fact]
	public async Task A_worker_without_audit_permission_cannot_search_audit_history()
	{
		var (_, _, workerId) = await SeedEventsAsync();
		var sut = new AuditQueries(CreateAuditQueryPort(database.ConnectionString));

		var act = () => sut.SearchAuditEventsAsync(new() { Context = ContextFor(workerId), Filter = new() });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Results_are_ordered_most_recent_first()
	{
		var (auditorId, _, _) = await SeedEventsAsync();
		var sut = new AuditQueries(CreateAuditQueryPort(database.ConnectionString));

		var results = await sut.SearchAuditEventsAsync(new() { Context = ContextFor(auditorId), Filter = new() });

		results.Events.Should().HaveCount(2);
		results.Events[0].OccurredAt.Should().BeGreaterThan(results.Events[1].OccurredAt);
	}

	[Fact]
	public async Task Paging_covers_every_matching_event_exactly_once_without_gaps_or_overlap()
	{
		const int eventCount = 5;
		var (auditorId, entityIds) = await SeedAuditorAndManyEventsAsync(
			eventCount, index => At(1 + index), index => "job_node", index => 100 + index);
		var sut = new AuditQueries(CreateAuditQueryPort(database.ConnectionString));

		var seenIds = new List<long>();
		string? cursor = null;
		do {
			var page = await sut.SearchAuditEventsAsync(new() {
				Context = ContextFor(auditorId),
				Filter = new() { EntityType = "job_node" },
				PageSize = 2,
				Cursor = cursor,
			});
			page.Events.Count.Should().BeLessThanOrEqualTo(2);
			seenIds.AddRange(page.Events.Select(e => e.EntityId));
			cursor = page.ContinuationCursor;
		} while (cursor is not null);

		seenIds.Should().BeEquivalentTo(entityIds);
		seenIds.Distinct().Should().HaveCount(eventCount);
	}

	[Fact]
	public async Task Equal_timestamp_events_are_tie_broken_deterministically_across_pages()
	{
		const int eventCount = 4;
		var tied = At(5);
		var (auditorId, entityIds) = await SeedAuditorAndManyEventsAsync(
			eventCount, _ => tied, _ => "job_node", index => 200 + index);
		var sut = new AuditQueries(CreateAuditQueryPort(database.ConnectionString));

		var firstPage = await sut.SearchAuditEventsAsync(new() {
			Context = ContextFor(auditorId),
			Filter = new() { EntityType = "job_node" },
			PageSize = 2,
		});
		var secondPage = await sut.SearchAuditEventsAsync(new() {
			Context = ContextFor(auditorId),
			Filter = new() { EntityType = "job_node" },
			PageSize = 2,
			Cursor = firstPage.ContinuationCursor,
		});

		firstPage.Events.Should().HaveCount(2);
		secondPage.Events.Should().HaveCount(2);
		secondPage.ContinuationCursor.Should().BeNull();
		var pagedIds = firstPage.Events.Concat(secondPage.Events).Select(e => e.EntityId).ToArray();
		pagedIds.Should().BeEquivalentTo(entityIds);
		pagedIds.Distinct().Should().HaveCount(eventCount);
	}

	[Fact]
	public async Task The_entity_type_filter_narrows_before_the_page_limit_is_applied()
	{
		var (auditorId, jobNodeEntityIds) = await SeedAuditorAndManyEventsAsync(
			3, index => At(1 + (index * 2)), index => "job_node", index => 300 + index,
			index => (At(2 + (index * 2)), "user_schedule_version", 900 + index));
		var sut = new AuditQueries(CreateAuditQueryPort(database.ConnectionString));

		var page = await sut.SearchAuditEventsAsync(new() {
			Context = ContextFor(auditorId),
			Filter = new() { EntityType = "job_node" },
			PageSize = 10,
		});

		page.Events.Should().HaveCount(3);
		page.Events.Select(e => e.EntityId).Should().BeEquivalentTo(jobNodeEntityIds);
		page.ContinuationCursor.Should().BeNull();
	}

	[Fact]
	public async Task A_page_from_a_large_result_set_only_materializes_the_requested_page_size()
	{
		const int totalEvents = 250;
		var (auditorId, _) = await SeedAuditorAndManyEventsAsync(
			totalEvents, index => At(1) + Duration.FromSeconds(index), index => "job_node", index => 400 + index);
		var sut = new AuditQueries(CreateAuditQueryPort(database.ConnectionString));

		var page = await sut.SearchAuditEventsAsync(new() {
			Context = ContextFor(auditorId),
			Filter = new() { EntityType = "job_node" },
			PageSize = 10,
		});

		page.Events.Should().HaveCount(10);
		page.ContinuationCursor.Should().NotBeNull();
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	internal abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	internal abstract IAuditQueryPort CreateAuditQueryPort(string connectionString);

	/// <summary>
	///     Seeds a deployed schema, an administrator via the real bootstrap port (granted
	///     <see cref="EmployeeRole.Auditor" />), one employee additionally granted
	///     <see cref="EmployeeRole.CostViewer" />, one plain <see cref="EmployeeRole.Worker" /> employee,
	///     and two <c>audit_event</c> rows: a non-sensitive <c>job_node</c> event at 09:00 and a
	///     sensitive <c>user_cost_rate</c> event at 10:00, both performed by a fourth employee.
	/// </summary>
	private async Task<(AppUserId AuditorId, AppUserId CostViewerAuditorId, AppUserId WorkerId)> SeedEventsAsync()
	{
		await using (var connection = await OpenExistingConnectionAsync()) {
			var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
			var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);
		}

		var bootstrapPort = CreateBootstrapPort(database.ConnectionString);
		_ = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Ada Lovelace",
			IanaTimeZone = "Europe/London",
			UserName = "ada.lovelace",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});

		// A separate employee, not the bootstrap administrator, holds only Auditor: the
		// administrator now always also holds EmployeeRole.Administrator (bootstrap grants it),
		// which alone satisfies CostAccessPolicy.CanView and would defeat this fixture's
		// "auditor without cost visibility" scenario.
		var auditorId = await SeedEmployeeAsync("Rosalind Franklin", "rosalind.franklin.audit", EmployeeRole.Auditor);

		var costViewerAuditorId = await SeedEmployeeAsync("Katherine Johnson", "katherine.johnson.audit", EmployeeRole.Auditor);
		await using (var connection = await OpenExistingConnectionAsync()) {
			await AssignRoleAsync(connection, costViewerAuditorId, EmployeeRole.CostViewer);
		}

		var workerId = await SeedEmployeeAsync("Grace Hopper", "grace.hopper.audit", EmployeeRole.Worker);
		var actorId = await SeedEmployeeAsync("Margaret Hamilton", "margaret.hamilton.audit", EmployeeRole.Worker);

		await InsertAuditEventAsync(
			actorId, At(9), "create-job-node", "job_node", 42, Guid.NewGuid(),
			"""{"description":"Do the thing"}""");
		await InsertAuditEventAsync(
			actorId, At(10), "add-user-cost-rate", "user_cost_rate", 7, Guid.NewGuid(),
			"""{"amount_per_hour":"60.00"}""");

		return (auditorId, costViewerAuditorId, workerId);
	}

	/// <summary>
	///     Seeds a deployed schema, one <see cref="EmployeeRole.Auditor" /> employee, one actor employee,
	///     and <paramref name="count" /> audit events (one per index, via <paramref name="entityTypeAt" />/
	///     <paramref name="entityIdAt" />/<paramref name="occurredAtAt" />), optionally interleaving an
	///     additional non-matching event per index via <paramref name="interleaveWith" /> (fresh-eyes
	///     review §2.3's paging/filter-before-limit coverage).
	/// </summary>
	private async Task<(AppUserId AuditorId, IReadOnlyList<long> EntityIds)> SeedAuditorAndManyEventsAsync(
		int count,
		Func<int, Instant> occurredAtAt,
		Func<int, string> entityTypeAt,
		Func<int, long> entityIdAt,
		Func<int, (Instant OccurredAt, string EntityType, long EntityId)>? interleaveWith = null)
	{
		await using (var connection = await OpenExistingConnectionAsync()) {
			var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
			var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);
		}

		var bootstrapPort = CreateBootstrapPort(database.ConnectionString);
		_ = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Ada Lovelace",
			IanaTimeZone = "Europe/London",
			UserName = "ada.lovelace",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});

		var auditorId = await SeedEmployeeAsync("Rosalind Franklin", "rosalind.franklin.audit", EmployeeRole.Auditor);
		var actorId = await SeedEmployeeAsync("Margaret Hamilton", "margaret.hamilton.audit", EmployeeRole.Worker);

		var entityIds = new List<long>(count);
		for (var index = 0; index < count; index++) {
			var entityId = entityIdAt(index);
			entityIds.Add(entityId);
			await InsertAuditEventAsync(
				actorId, occurredAtAt(index), "create-job-node", entityTypeAt(index), entityId, Guid.NewGuid(), "{}");

			if (interleaveWith is not null) {
				var (extraOccurredAt, extraEntityType, extraEntityId) = interleaveWith(index);
				await InsertAuditEventAsync(actorId, extraOccurredAt, "add-request-note", extraEntityType, extraEntityId, Guid.NewGuid(), "{}");
			}
		}

		return (auditorId, entityIds);
	}

	private async Task InsertAuditEventAsync(
		AppUserId actorId, Instant occurredAt, string operation, string entityType, long entityId, Guid correlationId, string afterDataJson)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();

		if (Provider == SchemaProvider.PostgreSql) {
			command.CommandText = """
								  INSERT INTO audit_event
								  	(occurred_at, actor_user_id, operation, entity_type, entity_id, correlation_id, reason, before_data, after_data)
								  VALUES
								  	(@occurredAt, @actorUserId, @operation, @entityType, @entityId, @correlationId, NULL, NULL, @afterData::jsonb);
								  """;
			AddParameter(command, "@occurredAt", occurredAt.ToDateTimeOffset());
			AddParameter(command, "@correlationId", correlationId);
		} else {
			command.CommandText = """
								  INSERT INTO audit_event
								  	(occurred_at, actor_user_id, operation, entity_type, entity_id, correlation_id, reason, before_data, after_data)
								  VALUES
								  	(@occurredAt, @actorUserId, @operation, @entityType, @entityId, @correlationId, NULL, NULL, @afterData);
								  """;
			AddParameter(command, "@occurredAt", occurredAt.ToUnixTimeTicks());
			AddParameter(command, "@correlationId", correlationId.ToString());
		}

		AddParameter(command, "@actorUserId", actorId.Value);
		AddParameter(command, "@operation", operation);
		AddParameter(command, "@entityType", entityType);
		AddParameter(command, "@entityId", entityId);
		AddParameter(command, "@afterData", afterDataJson);
		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task<AppUserId> SeedEmployeeAsync(string displayName, string userName, EmployeeRole role)
	{
		await using var connection = await OpenExistingConnectionAsync();

		await using var appUserCommand = connection.CreateCommand();
		appUserCommand.CommandText = """
									 INSERT INTO app_user (display_name, iana_time_zone)
									 VALUES (@displayName, 'Europe/London')
									 RETURNING id;
									 """;
		AddParameter(appUserCommand, "@displayName", displayName);
		var appUserId = new AppUserId(Convert.ToInt64(await appUserCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture));

		await using var identityUserCommand = connection.CreateCommand();
		identityUserCommand.CommandText = """
										  INSERT INTO identity_user
										  	(app_user_id, user_name, normalized_user_name, password_hash, security_stamp,
										  	 concurrency_stamp, requires_password_change, is_enabled, lockout_enabled, access_failed_count)
										  VALUES
										  	(@appUserId, @userName, @normalizedUserName, 'test-hash', @securityStamp,
										  	 @concurrencyStamp, @requiresPasswordChange, @isEnabled, @lockoutEnabled, 0);
										  """;
		AddParameter(identityUserCommand, "@appUserId", appUserId.Value);
		AddParameter(identityUserCommand, "@userName", userName);
		AddParameter(identityUserCommand, "@normalizedUserName", userName.ToUpperInvariant());
		AddParameter(identityUserCommand, "@securityStamp", Guid.NewGuid().ToString("N"));
		AddParameter(identityUserCommand, "@concurrencyStamp", Guid.NewGuid().ToString("N"));
		AddParameter(identityUserCommand, "@requiresPasswordChange", false);
		AddParameter(identityUserCommand, "@isEnabled", true);
		AddParameter(identityUserCommand, "@lockoutEnabled", true);
		_ = await identityUserCommand.ExecuteNonQueryAsync();

		await AssignRoleAsync(connection, appUserId, role);

		return appUserId;
	}

	private static async Task AssignRoleAsync(DbConnection connection, AppUserId appUserId, EmployeeRole role)
	{
		await using var roleCommand = connection.CreateCommand();
		roleCommand.CommandText = """
								  INSERT INTO identity_user_role (identity_user_id, identity_role_id)
								  SELECT id, @roleId FROM identity_user WHERE app_user_id = @appUserId;
								  """;
		AddParameter(roleCommand, "@appUserId", appUserId.Value);
		AddParameter(roleCommand, "@roleId", (short)role);
		_ = await roleCommand.ExecuteNonQueryAsync();
	}

	private async Task<DbConnection> OpenExistingConnectionAsync()
	{
		var connection = CreateConnection(database.ConnectionString);
		await connection.OpenAsync();
		await PrepareConnectionAsync(connection);
		return connection;
	}

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}
