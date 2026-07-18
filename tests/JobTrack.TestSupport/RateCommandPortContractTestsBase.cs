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
///     Shared contract for <see cref="IRateCommandPort" /> (impl plan §7.4 step 3, §7.3 slice 9: add
///     user rates and node overrides), asserted identically against PostgreSQL and SQLite by one thin
///     sealed subclass per provider's own test project -- same shape as
///     <see cref="ScheduleCommandPortContractTestsBase" />. Mirrors <c>RateCommandsTests</c>' scenarios
///     against the fake port, so the real persistence implementations are held to the same behavioural
///     contract. Node rate overrides target the permanent root job node the real bootstrap port
///     already creates, so no separate job-node seeding step is needed.
/// </summary>
public abstract class RateCommandPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected RateCommandPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task A_rate_manager_can_add_a_user_cost_rate()
	{
		var (_, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);

		var result = await port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		result.UserId.Should().Be(workerId);
		result.Version.Should().Be(1);
	}

	[Fact]
	public async Task Adding_a_user_cost_rate_writes_an_audit_event()
	{
		var (_, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		var result = await port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			rateManagerId, new() { EntityType = "user_cost_rate", EntityId = result.Id.Value });

		audit.Events.Should().ContainSingle();
		audit.Events[0].Operation.Should().Be("add-user-cost-rate");
		audit.Events[0].ActorId.Should().Be(rateManagerId);
		audit.Events[0].AfterData!.Value["amount_per_hour"].Should().Be("25");
	}

	[Fact]
	public async Task A_worker_cannot_add_a_user_cost_rate()
	{
		var (_, _, _, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);

		var act = () => port.AddUserCostRateAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Adding_a_user_cost_rate_for_a_nonexistent_employee_throws_not_found()
	{
		var (_, _, rateManagerId, _) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);

		var act = () => port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = new(rateManagerId.Value + 999),
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Overlapping_user_cost_rates_for_the_same_employee_throw_an_invariant_violation()
	{
		var (_, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		_ = await port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Rate = new(
				new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 6, 1, 0, 0)),
		});

		var act = () => port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Rate = new(new(30m), Instant.FromUtc(2026, 3, 1, 0, 0), null),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("user-cost-rate-overlap");
	}

	[Fact]
	public async Task An_administrator_can_add_a_node_rate_override()
	{
		var (rootId, administratorId, _, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);

		var result = await port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Override = new(rootId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		result.UserId.Should().Be(workerId);
		result.Override.NodeId.Should().Be(rootId);
	}

	[Fact]
	public async Task A_worker_cannot_add_a_node_rate_override()
	{
		var (rootId, _, _, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);

		var act = () => port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Override = new(rootId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Adding_a_node_rate_override_for_a_nonexistent_node_throws_not_found()
	{
		var (rootId, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);

		var act = () => port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Override = new(
				new(rootId.Value + 999), new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Overlapping_node_rate_overrides_for_the_same_node_and_employee_throw_an_invariant_violation()
	{
		var (rootId, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		_ = await port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Override = new(
				rootId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 6, 1, 0, 0)),
		});

		var act = () => port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Override = new(rootId, new(45m), Instant.FromUtc(2026, 3, 1, 0, 0), null),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("node-rate-override-overlap");
	}

	[Fact]
	public async Task A_rate_manager_can_correct_a_user_cost_rate()
	{
		var (_, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		var added = await port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var result = await port.CorrectUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			RateId = added.Id,
			UserId = workerId,
			Version = added.Version,
			Reason = "Corrected the agreed rate",
			Rate = new(new(30m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		result.Rate.Rate.AmountPerHour.Should().Be(30m);
		result.Version.Should().Be(added.Version + 1);
	}

	[Fact]
	public async Task Correcting_a_user_cost_rate_writes_an_audit_event()
	{
		var (_, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		var added = await port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		_ = await port.CorrectUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			RateId = added.Id,
			UserId = workerId,
			Version = added.Version,
			Reason = "Corrected the agreed rate",
			Rate = new(new(30m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			rateManagerId, new() { EntityType = "user_cost_rate", EntityId = added.Id.Value });

		audit.Events.Should().Contain(e => e.Operation == "correct-user-cost-rate" && e.ActorId == rateManagerId);
	}

	[Fact]
	public async Task Correcting_a_user_cost_rate_with_a_stale_version_throws_a_concurrency_conflict()
	{
		var (_, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		var added = await port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var act = () => port.CorrectUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			RateId = added.Id,
			UserId = workerId,
			Version = added.Version + 1,
			Reason = "Corrected the agreed rate",
			Rate = new(new(30m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	[Fact]
	public async Task Correcting_a_user_cost_rate_into_overlap_with_another_throws_an_invariant_violation()
	{
		var (_, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		_ = await port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Rate = new(
				new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 3, 1, 0, 0)),
		});
		var toCorrect = await port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Rate = new(new(30m), Instant.FromUtc(2026, 6, 1, 0, 0), null),
		});

		var act = () => port.CorrectUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			RateId = toCorrect.Id,
			UserId = workerId,
			Version = toCorrect.Version,
			Reason = "Moved the start date earlier",
			Rate = new(new(30m), Instant.FromUtc(2026, 2, 1, 0, 0), null),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("user-cost-rate-overlap");
	}

	[Fact]
	public async Task A_worker_cannot_correct_a_user_cost_rate()
	{
		var (_, _, rateManagerId, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		var added = await port.AddUserCostRateAsync(new() {
			Context = ContextFor(rateManagerId),
			UserId = workerId,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var act = () => port.CorrectUserCostRateAsync(new() {
			Context = ContextFor(workerId),
			RateId = added.Id,
			UserId = workerId,
			Version = added.Version,
			Reason = "Attempted correction",
			Rate = new(new(30m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task An_administrator_can_correct_a_node_rate_override()
	{
		var (rootId, administratorId, _, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		var added = await port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Override = new(rootId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var result = await port.CorrectNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			OverrideId = added.Id,
			UserId = workerId,
			Version = added.Version,
			Reason = "Corrected the override rate",
			Override = new(rootId, new(45m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		result.Override.Rate.AmountPerHour.Should().Be(45m);
		result.Version.Should().Be(added.Version + 1);
	}

	[Fact]
	public async Task Correcting_a_node_rate_override_writes_an_audit_event()
	{
		var (rootId, administratorId, _, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		var added = await port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Override = new(rootId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		_ = await port.CorrectNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			OverrideId = added.Id,
			UserId = workerId,
			Version = added.Version,
			Reason = "Corrected the override rate",
			Override = new(rootId, new(45m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			administratorId, new() { EntityType = "node_rate_override", EntityId = added.Id.Value });

		audit.Events.Should().Contain(e => e.Operation == "correct-node-rate-override" && e.ActorId == administratorId);
	}

	[Fact]
	public async Task Correcting_a_node_rate_override_with_a_stale_version_throws_a_concurrency_conflict()
	{
		var (rootId, administratorId, _, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		var added = await port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Override = new(rootId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		var act = () => port.CorrectNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			OverrideId = added.Id,
			UserId = workerId,
			Version = added.Version + 1,
			Reason = "Corrected the override rate",
			Override = new(rootId, new(45m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
		});

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	[Fact]
	public async Task Correcting_a_node_rate_override_into_overlap_with_another_throws_an_invariant_violation()
	{
		var (rootId, administratorId, _, workerId) = await SeedAdministratorRateManagerAndWorkerAsync();
		var port = CreateRatePort(database.ConnectionString);
		_ = await port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Override = new(
				rootId, new(40m), Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 3, 1, 0, 0)),
		});
		var toCorrect = await port.AddNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Override = new(rootId, new(45m), Instant.FromUtc(2026, 6, 1, 0, 0), null),
		});

		var act = () => port.CorrectNodeRateOverrideAsync(new() {
			Context = ContextFor(administratorId),
			OverrideId = toCorrect.Id,
			UserId = workerId,
			Version = toCorrect.Version,
			Reason = "Moved the start date earlier",
			Override = new(rootId, new(45m), Instant.FromUtc(2026, 2, 1, 0, 0), null),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("node-rate-override-overlap");
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	protected abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	protected abstract IRateCommandPort CreateRatePort(string connectionString);

	protected abstract IAuditQueryPort CreateAuditQueryPort(string connectionString);

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	/// <summary>
	///     Seeds a deployed schema, an administrator via the real bootstrap port (which also
	///     creates the permanent root job node and grants <see cref="EmployeeRole.Administrator" />),
	///     a <see cref="EmployeeRole.RateManager" /> employee, and one <see cref="EmployeeRole.Worker" />
	///     employee.
	/// </summary>
	private async Task<(JobNodeId RootId, AppUserId AdministratorId, AppUserId RateManagerId, AppUserId WorkerId)>
		SeedAdministratorRateManagerAndWorkerAsync()
	{
		await using (var connection = await OpenExistingConnectionAsync()) {
			var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
			var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);
		}

		var bootstrapPort = CreateBootstrapPort(database.ConnectionString);
		var result = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Ada Lovelace",
			IanaTimeZone = "Europe/London",
			UserName = "ada.lovelace",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});

		var rateManagerId = await SeedEmployeeAsync("Katherine Johnson", "katherine.johnson.rate", EmployeeRole.RateManager);
		var workerId = await SeedEmployeeAsync("Grace Hopper", "grace.hopper.rate", EmployeeRole.Worker);

		return (result.RootJobNodeId, result.AdministratorId, rateManagerId, workerId);
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
