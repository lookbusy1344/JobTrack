namespace JobTrack.TestSupport;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using Domain.Schedules;
using NodaTime;

/// <summary>
///     Shared contract for <see cref="ICostQueryPort" /> (impl plan §7.4 step 3, §7.3 slice 10:
///     calculate cost details and hierarchy totals), asserted identically against PostgreSQL and
///     SQLite by one thin sealed subclass per provider's own test project -- same shape as
///     <see cref="ScheduleCommandPortContractTestsBase" />. Exercises the real port through
///     <see
///         cref="CostQueries" />
///     (not called directly), the same way <c>CostQueriesTests</c> exercises the
///     fake port, so a passing run proves the real persistence-materialized inputs reproduce the exact
///     dollar amounts and the ADR 0017 exposure boundary the application-layer contract already
///     establishes. <see cref="IWorkSessionCommandPort.CorrectSessionAsync" /> pins each session to
///     deterministic historical instants -- <see cref="IWorkSessionCommandPort.StartSessionAsync" />
///     itself captures the real clock, which a repeatable cost assertion cannot depend on.
/// </summary>
public abstract class CostQueryPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected CostQueryPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	private static Instant At(int hour) => hour == 24 ? Instant.FromUtc(2026, 1, 2, 0, 0) : Instant.FromUtc(2026, 1, 1, hour, 0);

	[Fact]
	public async Task A_cost_viewer_can_calculate_cost_details_for_a_leaf()
	{
		var (_, _, leafId, _, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));
		var sut = new CostQueries(CreateCostQueryPort(database.ConnectionString));

		var result = await sut.GetCostDetailsAsync(new() { Context = ContextFor(administratorId), NodeId = leafId, AsOf = At(24) });

		result.NodeId.Should().Be(leafId);
		result.ExactCost.Should().Be(new Money(120m));
		result.DisplayedCost.Should().Be(new Money(120m));
		result.Trace.Should().OnlyContain(entry => entry.NodeId == leafId);
	}

	[Fact]
	public async Task A_recurring_schedule_is_expanded_only_across_the_relevant_session_range()
	{
		var (_, _, leafId, _, administratorId, workerId) = await SeedTreeAsync();
		var schedulePort = CreateSchedulePort(database.ConnectionString);
		_ = await schedulePort.AddScheduleVersionAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Schedule = new(
				DateTimeZoneProviders.Tzdb["Europe/London"],
				new(2026, 1, 1),
				null,
				[new(IsoDayOfWeek.Thursday, new(9, 0), new(17, 0))]),
		});
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));
		var sut = new CostQueries(CreateCostQueryPort(database.ConnectionString));

		var result = await sut.GetCostDetailsAsync(new() { Context = ContextFor(administratorId), NodeId = leafId, AsOf = At(24) });

		result.ExactCost.Should().Be(new Money(120m));
	}

	/// <summary>
	///     <c>branchId</c> and the root are administrator-owned (see <c>SeedTreeAsync</c>), so ADR 0040's
	///     ownership carve-out does not apply here -- distinct from
	///     <see cref="A_worker_may_view_cost_details_for_a_node_they_own_despite_no_qualifying_role" />,
	///     which exercises that carve-out on <c>leafId</c>.
	/// </summary>
	[Fact]
	public async Task A_worker_without_cost_viewing_permission_or_ownership_cannot_calculate_cost_details()
	{
		var (_, branchId, leafId, _, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));
		var sut = new CostQueries(CreateCostQueryPort(database.ConnectionString));

		var act = () => sut.GetCostDetailsAsync(new() { Context = ContextFor(workerId), NodeId = branchId, AsOf = At(24) });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	/// <summary>
	///     ADR 0040: a worker with no cost-viewing role may still view cost details for a node they own directly (<c>leafId</c>, owned by
	///     <c>workerId</c> per <c>SeedTreeAsync</c>).
	/// </summary>
	[Fact]
	public async Task A_worker_may_view_cost_details_for_a_node_they_own_despite_no_qualifying_role()
	{
		var (_, _, leafId, _, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));
		var sut = new CostQueries(CreateCostQueryPort(database.ConnectionString));

		var result = await sut.GetCostDetailsAsync(new() { Context = ContextFor(workerId), NodeId = leafId, AsOf = At(24) });

		result.ExactCost.Should().Be(new Money(120m));
	}

	[Fact]
	public async Task Hierarchy_totals_reflect_a_workers_foreign_concurrent_session_without_exposing_it()
	{
		var (_, branchId, leafId, otherLeafId, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));
		await CreateCorrectedSessionAsync(administratorId, workerId, otherLeafId, At(10), At(12));
		var sut = new CostQueries(CreateCostQueryPort(database.ConnectionString));

		var result = await sut.GetHierarchyTotalsAsync(new() { Context = ContextFor(administratorId), NodeId = branchId, AsOf = At(24) });

		// [09:00,10:00) session1 alone: 1h @ 60 = 60. [10:00,11:00) both sessions share: 0.5h @ 60 = 30. Total 90.
		result.ExactCosts.Should().ContainKeys(branchId, leafId);
		result.ExactCosts.Should().NotContainKey(otherLeafId);
		result.ExactCosts[leafId].Should().Be(new Money(90m));
		result.ExactCosts[branchId].Should().Be(new Money(90m));
		result.DisplayedCosts[branchId].Should().Be(new Money(90m));
		result.DisplayedCosts[leafId].Should().Be(new Money(90m));
	}

	[Fact]
	public async Task Calculating_cost_details_for_a_nonexistent_node_throws_not_found()
	{
		var (_, _, _, _, administratorId, _) = await SeedTreeAsync();
		var sut = new CostQueries(CreateCostQueryPort(database.ConnectionString));

		var act = () => sut.GetCostDetailsAsync(new() { Context = ContextFor(administratorId), NodeId = new(999_999), AsOf = At(24) });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetCostDetailsAsync_throws_a_domain_fault_when_a_stored_schedule_zone_id_is_no_longer_recognized()
	{
		var (_, _, leafId, _, administratorId, workerId) = await SeedTreeAsync();
		var schedulePort = CreateSchedulePort(database.ConnectionString);
		_ = await schedulePort.AddScheduleVersionAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Schedule = new(
				DateTimeZoneProviders.Tzdb["Europe/London"],
				new(2026, 1, 1),
				null,
				[new(IsoDayOfWeek.Thursday, new(0, 0), new(23, 59, 59))]),
		});
		await CorruptStoredScheduleZoneIdAsync(workerId, "Bogus/NotAZone");
		await AddUserCostRateAsync(administratorId, workerId, new(60m));
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));
		var sut = new CostQueries(CreateCostQueryPort(database.ConnectionString));

		var act = () => sut.GetCostDetailsAsync(new() { Context = ContextFor(administratorId), NodeId = leafId, AsOf = At(24) });

		await act.Should().ThrowAsync<UnknownStoredTimeZoneException>();
	}

	[Fact]
	public async Task A_session_with_no_resolvable_rate_throws_missing_rate()
	{
		var (_, _, leafId, _, administratorId, workerId) = await SeedTreeAsync();
		await GiveWorkerFullDayWorkingTimeAsync(administratorId, workerId);
		await CreateCorrectedSessionAsync(administratorId, workerId, leafId, At(9), At(11));
		var sut = new CostQueries(CreateCostQueryPort(database.ConnectionString));

		var act = () => sut.GetCostDetailsAsync(new() { Context = ContextFor(administratorId), NodeId = leafId, AsOf = At(24) });

		await act.Should().ThrowAsync<MissingRateException>();
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	protected abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	protected abstract IJobNodeCommandPort CreateJobNodePort(string connectionString);

	protected abstract IScheduleCommandPort CreateSchedulePort(string connectionString);

	protected abstract IRateCommandPort CreateRatePort(string connectionString);

	protected abstract IWorkSessionCommandPort CreateSessionPort(string connectionString);

	protected abstract ICostQueryPort CreateCostQueryPort(string connectionString);

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	private async Task GiveWorkerFullDayWorkingTimeAsync(AppUserId administratorId, AppUserId workerId)
	{
		var schedulePort = CreateSchedulePort(database.ConnectionString);
		_ = await schedulePort.AddScheduleExceptionAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Entry = new(ScheduleExceptionEffect.AddWorkingTime, new(At(0), At(24)), null),
			Reason = "Full working day for cost-query contract tests",
		});
	}

	private async Task CorruptStoredScheduleZoneIdAsync(AppUserId workerId, string ianaTimeZone)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "UPDATE user_schedule_version SET iana_time_zone = @ianaTimeZone WHERE user_id = @userId;";
		AddParameter(command, "@ianaTimeZone", ianaTimeZone);
		AddParameter(command, "@userId", workerId.Value);
		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task AddUserCostRateAsync(AppUserId administratorId, AppUserId workerId, HourlyRate rate)
	{
		var ratePort = CreateRatePort(database.ConnectionString);
		_ = await ratePort.AddUserCostRateAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Rate = new(rate, Instant.FromUtc(2000, 1, 1, 0, 0), null),
		});
	}

	private async Task CreateCorrectedSessionAsync(
		AppUserId administratorId, AppUserId workerId, JobNodeId leafId, Instant startedAt, Instant finishedAt)
	{
		var sessionPort = CreateSessionPort(database.ConnectionString);
		var session = await sessionPort.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = workerId });

		_ = await sessionPort.CorrectSessionAsync(new() {
			Context = ContextFor(administratorId),
			SessionId = session.Id,
			StartedAt = startedAt,
			FinishedAt = finishedAt,
			Reason = "Pin to a deterministic instant for cost-query contract tests",
			Version = session.Version,
		});
	}

	/// <summary>
	///     Seeds a deployed schema, an administrator via the real bootstrap port (which
	///     itself grants <see cref="EmployeeRole.Administrator" />, satisfying every policy this
	///     slice's dependent ports check), one <see cref="EmployeeRole.Worker" /> employee, and a tree shaped
	///     like <c>CostQueriesTests</c>' own fixture: root with children [branch, otherLeaf], branch
	///     with child [leaf].
	/// </summary>
	private async Task<(JobNodeId RootId, JobNodeId BranchId, JobNodeId LeafId, JobNodeId OtherLeafId, AppUserId AdministratorId, AppUserId WorkerId)>
		SeedTreeAsync()
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

		var workerId = await SeedEmployeeAsync("Grace Hopper", "grace.hopper.cost", EmployeeRole.Worker);

		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var branch = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(result.AdministratorId),
			ParentId = result.RootJobNodeId,
			Description = "Branch",
			OwnerUserId = result.AdministratorId,
			Priority = Priority.Medium,
		});
		var leaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(result.AdministratorId),
			ParentId = branch.Id,
			Description = "Leaf",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(result.AdministratorId), JobNodeId = leaf.Id });
		var otherLeaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(result.AdministratorId),
			ParentId = result.RootJobNodeId,
			Description = "Other leaf",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(
			new() { Context = ContextFor(result.AdministratorId), JobNodeId = otherLeaf.Id });

		return (result.RootJobNodeId, branch.Id, leaf.Id, otherLeaf.Id, result.AdministratorId, workerId);
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
