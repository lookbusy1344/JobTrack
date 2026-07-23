namespace JobTrack.TestSupport;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using Microsoft.AspNetCore.Identity;
using NodaTime;
using NodaTime.TimeZones;

/// <summary>
///     Shared contract for <see cref="IEmployeeCommandPort" /> (plan §8.3: employee role assignment),
///     asserted identically against PostgreSQL and SQLite by one thin sealed subclass per provider's
///     own test project — same shape as <see cref="RateCommandPortContractTestsBase" />.
/// </summary>
public abstract class EmployeeCommandPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private const short PriorityMedium = 2;

	private readonly IDisposableTestDatabase database;

	protected EmployeeCommandPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	private EmployeeCommands CreateSut() => new(CreateCommandPort(database.ConnectionString), new PasswordHasher<EmployeeCredentialSubject>());

	[Fact]
	public async Task An_administrator_can_create_a_new_employee()
	{
		var (administratorId, _) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var result = await sut.CreateEmployeeAsync(new() {
			Context = ContextFor(administratorId),
			DisplayName = "Katherine Johnson",
			IanaTimeZone = "Europe/London",
			UserName = "katherine.johnson",
			Password = "correct-horse-battery-staple",
			Role = EmployeeRole.Worker,
		});

		result.UserName.Should().Be("katherine.johnson");
		result.IsEnabled.Should().BeTrue();
		result.RequiresPasswordChange.Should().BeTrue();
		result.Roles.Should().ContainSingle().Which.Should().Be(EmployeeRole.Worker);
	}

	[Fact]
	public async Task A_worker_cannot_create_a_new_employee()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var act = () => sut.CreateEmployeeAsync(new() {
			Context = ContextFor(workerId),
			DisplayName = "Katherine Johnson",
			IanaTimeZone = "Europe/London",
			UserName = "katherine.johnson",
			Password = "correct-horse-battery-staple",
			Role = EmployeeRole.Worker,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Creating_an_employee_persists_the_canonical_zone_id()
	{
		var (administratorId, _) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var result = await sut.CreateEmployeeAsync(new() {
			Context = ContextFor(administratorId),
			DisplayName = "Srinivasa Ramanujan",
			IanaTimeZone = "Asia/Calcutta",
			UserName = "srinivasa.ramanujan",
			Password = "correct-horse-battery-staple",
			Role = EmployeeRole.Worker,
		});

		var storedZoneId = await GetIanaTimeZoneAsync(result.Id);

		storedZoneId.Should().Be("Asia/Kolkata");
	}

	[Fact]
	public async Task Creating_an_employee_without_an_explicit_rate_persists_the_default_rate_and_schedule()
	{
		var (administratorId, _) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var result = await sut.CreateEmployeeAsync(new() {
			Context = ContextFor(administratorId),
			DisplayName = "Default Provisioning",
			IanaTimeZone = "Europe/London",
			UserName = "default.provisioning",
			Password = "correct-horse-battery-staple",
			Role = EmployeeRole.Worker,
		});

		(await GetDefaultHourlyRateAsync(result.Id)).Should().Be(20m);
		var schedule = await GetOnlyScheduleAsync(result.Id);
		schedule.EffectiveStart.Should().Be("2020-01-01");
		schedule.EffectiveEnd.Should().BeNull();
		schedule.IanaTimeZone.Should().Be("Europe/London");
		schedule.Intervals.Should().Equal(new ScheduleIntervalSummary(1, "09:00:00", "17:00:00", false),
			new ScheduleIntervalSummary(2, "09:00:00", "17:00:00", false), new ScheduleIntervalSummary(3, "09:00:00", "17:00:00", false),
			new ScheduleIntervalSummary(4, "09:00:00", "17:00:00", false), new ScheduleIntervalSummary(5, "09:00:00", "17:00:00", false));
	}

	[Fact]
	public async Task Creating_an_employee_with_an_unrecognized_zone_id_throws()
	{
		var (administratorId, _) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var act = () => sut.CreateEmployeeAsync(new() {
			Context = ContextFor(administratorId),
			DisplayName = "Bad Zone",
			IanaTimeZone = "Bogus/NotAZone",
			UserName = "bad.zone",
			Password = "correct-horse-battery-staple",
			Role = EmployeeRole.Worker,
		});

		await act.Should().ThrowAsync<DateTimeZoneNotFoundException>();
	}

	[Fact]
	public async Task Creating_an_employee_with_a_taken_username_throws_invariant_violation()
	{
		var (administratorId, workerId) = await SeedAdministratorAndWorkerAsync();
		var takenUserName = await GetUserNameAsync(workerId);
		var sut = CreateSut();

		var act = () => sut.CreateEmployeeAsync(new() {
			Context = ContextFor(administratorId),
			DisplayName = "Duplicate Username",
			IanaTimeZone = "Europe/London",
			UserName = takenUserName,
			Password = "correct-horse-battery-staple",
			Role = EmployeeRole.Worker,
		});

		await act.Should().ThrowAsync<InvariantViolationException>();
	}

	[Fact]
	public async Task An_administrator_can_provision_a_second_administrator()
	{
		var (administratorId, _) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var result = await sut.CreateEmployeeAsync(new() {
			Context = ContextFor(administratorId),
			DisplayName = "Second Administrator",
			IanaTimeZone = "Europe/London",
			UserName = "second.administrator",
			Password = "correct-horse-battery-staple",
			Role = EmployeeRole.Administrator,
		});

		result.Roles.Should().ContainSingle().Which.Should().Be(EmployeeRole.Administrator);
	}

	[Fact]
	public async Task An_administrator_can_assign_a_role()
	{
		var (administratorId, workerId) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var result = await sut.AssignRoleAsync(new() {
			Context = ContextFor(administratorId),
			TargetUserId = workerId,
			Role = EmployeeRole.RateManager,
		});

		result.UserId.Should().Be(workerId);
		result.Roles.Should().Contain([EmployeeRole.Worker, EmployeeRole.RateManager]);
	}

	[Fact]
	public async Task Assigning_an_already_held_role_is_idempotent()
	{
		var (administratorId, workerId) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var result = await sut.AssignRoleAsync(new() { Context = ContextFor(administratorId), TargetUserId = workerId, Role = EmployeeRole.Worker });

		result.Roles.Should().ContainSingle().Which.Should().Be(EmployeeRole.Worker);
	}

	[Fact]
	public async Task A_worker_cannot_assign_a_role()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var act = () => sut.AssignRoleAsync(new() { Context = ContextFor(workerId), TargetUserId = workerId, Role = EmployeeRole.RateManager });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Assigning_a_role_to_a_nonexistent_employee_throws_not_found()
	{
		var (administratorId, _) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var act = () => sut.AssignRoleAsync(new() {
			Context = ContextFor(administratorId),
			TargetUserId = new(administratorId.Value + 999),
			Role = EmployeeRole.Worker,
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task An_administrator_can_revoke_a_role()
	{
		var (administratorId, workerId) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();
		_ = await sut.AssignRoleAsync(new() { Context = ContextFor(administratorId), TargetUserId = workerId, Role = EmployeeRole.RateManager });

		var result = await sut.RevokeRoleAsync(new() {
			Context = ContextFor(administratorId),
			TargetUserId = workerId,
			Role = EmployeeRole.RateManager,
		});

		result.Roles.Should().ContainSingle().Which.Should().Be(EmployeeRole.Worker);
	}

	[Fact]
	public async Task Revoking_an_unheld_role_is_idempotent()
	{
		var (administratorId, workerId) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var result = await sut.RevokeRoleAsync(new() {
			Context = ContextFor(administratorId),
			TargetUserId = workerId,
			Role = EmployeeRole.RateManager,
		});

		result.Roles.Should().ContainSingle().Which.Should().Be(EmployeeRole.Worker);
	}

	[Fact]
	public async Task A_worker_cannot_revoke_a_role()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var act = () => sut.RevokeRoleAsync(new() { Context = ContextFor(workerId), TargetUserId = workerId, Role = EmployeeRole.Worker });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Revoking_a_role_from_a_nonexistent_employee_throws_not_found()
	{
		var (administratorId, _) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var act = () => sut.RevokeRoleAsync(new() {
			Context = ContextFor(administratorId),
			TargetUserId = new(administratorId.Value + 999),
			Role = EmployeeRole.Worker,
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task An_administrator_can_disable_an_account()
	{
		var (administratorId, workerId) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var result = await sut.SetEnabledAsync(new() { Context = ContextFor(administratorId), TargetUserId = workerId, Enabled = false });

		result.IsEnabled.Should().BeFalse();
	}

	[Fact]
	public async Task An_administrator_can_set_an_employees_default_hourly_rate()
	{
		var (administratorId, workerId) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var result = await sut.SetDefaultHourlyRateAsync(new() {
			Context = ContextFor(administratorId),
			TargetUserId = workerId,
			DefaultHourlyRate = new(30m),
		});

		result.Id.Should().Be(workerId);
		result.DefaultHourlyRate.Should().Be(new HourlyRate(30m));
		(await GetDefaultHourlyRateAsync(workerId)).Should().Be(30m);
	}

	[Fact]
	public async Task A_worker_cannot_set_an_employees_default_hourly_rate()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var act = () =>
			sut.SetDefaultHourlyRateAsync(new() { Context = ContextFor(workerId), TargetUserId = workerId, DefaultHourlyRate = new(30m) });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Setting_default_hourly_rate_for_a_nonexistent_employee_throws_not_found()
	{
		var (administratorId, _) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var act = () => sut.SetDefaultHourlyRateAsync(new() {
			Context = ContextFor(administratorId),
			TargetUserId = new(administratorId.Value + 999),
			DefaultHourlyRate = new(30m),
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Disabling_an_already_disabled_account_is_idempotent()
	{
		var (administratorId, workerId) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();
		_ = await sut.SetEnabledAsync(new() { Context = ContextFor(administratorId), TargetUserId = workerId, Enabled = false });

		var result = await sut.SetEnabledAsync(new() { Context = ContextFor(administratorId), TargetUserId = workerId, Enabled = false });

		result.IsEnabled.Should().BeFalse();
	}

	[Fact]
	public async Task A_worker_cannot_disable_an_account()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var act = () => sut.SetEnabledAsync(new() { Context = ContextFor(workerId), TargetUserId = workerId, Enabled = false });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Disabling_a_nonexistent_employee_throws_not_found()
	{
		var (administratorId, _) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var act = () => sut.SetEnabledAsync(new() {
			Context = ContextFor(administratorId),
			TargetUserId = new(administratorId.Value + 999),
			Enabled = false,
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Disabling_an_account_rotates_its_security_stamp()
	{
		var (administratorId, workerId) = await SeedAdministratorAndWorkerAsync();
		var stampBefore = await GetSecurityStampAsync(workerId);
		var sut = CreateSut();

		_ = await sut.SetEnabledAsync(new() { Context = ContextFor(administratorId), TargetUserId = workerId, Enabled = false });

		var stampAfter = await GetSecurityStampAsync(workerId);
		stampAfter.Should().NotBe(stampBefore);
	}

	[Fact]
	public async Task An_administrator_can_reset_a_password()
	{
		var (administratorId, workerId) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var result = await sut.ResetPasswordAsync(new() {
			Context = ContextFor(administratorId),
			TargetUserId = workerId,
			NewPassword = "correct-horse-battery-staple",
		});

		result.RequiresPasswordChange.Should().BeTrue();
	}

	[Fact]
	public async Task Resetting_a_password_rotates_the_security_stamp()
	{
		var (administratorId, workerId) = await SeedAdministratorAndWorkerAsync();
		var stampBefore = await GetSecurityStampAsync(workerId);
		var sut = CreateSut();

		_ = await sut.ResetPasswordAsync(new() {
			Context = ContextFor(administratorId),
			TargetUserId = workerId,
			NewPassword = "correct-horse-battery-staple",
		});

		var stampAfter = await GetSecurityStampAsync(workerId);
		stampAfter.Should().NotBe(stampBefore);
	}

	[Fact]
	public async Task A_worker_cannot_reset_a_password()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var act = () => sut.ResetPasswordAsync(new() {
			Context = ContextFor(workerId),
			TargetUserId = workerId,
			NewPassword = "correct-horse-battery-staple",
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Resetting_a_nonexistent_employees_password_throws_not_found()
	{
		var (administratorId, _) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var act = () => sut.ResetPasswordAsync(new() {
			Context = ContextFor(administratorId),
			TargetUserId = new(administratorId.Value + 999),
			NewPassword = "correct-horse-battery-staple",
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task An_administrator_can_reset_two_factor()
	{
		var (administratorId, workerId) = await SeedAdministratorAndWorkerAsync();
		await SeedTwoFactorEnabledAsync(workerId);
		var sut = CreateSut();

		_ = await sut.ResetTwoFactorAsync(new() { Context = ContextFor(administratorId), TargetUserId = workerId });

		var (enabled, keyProtected) = await GetTwoFactorStateAsync(workerId);
		enabled.Should().BeFalse();
		keyProtected.Should().BeNull();
	}

	[Fact]
	public async Task Resetting_two_factor_rotates_the_security_stamp()
	{
		var (administratorId, workerId) = await SeedAdministratorAndWorkerAsync();
		await SeedTwoFactorEnabledAsync(workerId);
		var stampBefore = await GetSecurityStampAsync(workerId);
		var sut = CreateSut();

		_ = await sut.ResetTwoFactorAsync(new() { Context = ContextFor(administratorId), TargetUserId = workerId });

		var stampAfter = await GetSecurityStampAsync(workerId);
		stampAfter.Should().NotBe(stampBefore);
	}

	[Fact]
	public async Task A_worker_cannot_reset_two_factor()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var act = () => sut.ResetTwoFactorAsync(new() { Context = ContextFor(workerId), TargetUserId = workerId });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Resetting_two_factor_for_a_nonexistent_employee_throws_not_found()
	{
		var (administratorId, _) = await SeedAdministratorAndWorkerAsync();
		var sut = CreateSut();

		var act = () => sut.ResetTwoFactorAsync(new() { Context = ContextFor(administratorId), TargetUserId = new(administratorId.Value + 999) });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Assigning_a_role_rotates_the_security_stamp()
	{
		var (administratorId, workerId) = await SeedAdministratorAndWorkerAsync();
		var stampBefore = await GetSecurityStampAsync(workerId);
		var sut = CreateSut();

		_ = await sut.AssignRoleAsync(new() { Context = ContextFor(administratorId), TargetUserId = workerId, Role = EmployeeRole.RateManager });

		var stampAfter = await GetSecurityStampAsync(workerId);
		stampAfter.Should().NotBe(stampBefore);
	}

	[Fact]
	public async Task An_employee_can_set_their_own_home_node_to_a_branch()
	{
		var (administratorId, workerId, rootId) = await SeedAdministratorAndWorkerWithRootAsync();
		var branchId = await SeedJobNodeAsync(rootId, administratorId);
		_ = await SeedJobNodeAsync(branchId, administratorId);
		var sut = CreateSut();

		var result = await sut.SetHomeNodeAsync(new() { Context = ContextFor(workerId), NodeId = branchId });

		result.HomeNodeId.Should().Be(branchId);
		(await GetHomeNodeIdAsync(workerId)).Should().Be(branchId.Value);
	}

	[Fact]
	public async Task An_employee_can_set_their_own_home_node_to_root()
	{
		var (administratorId, workerId, rootId) = await SeedAdministratorAndWorkerWithRootAsync();
		var sut = CreateSut();

		var result = await sut.SetHomeNodeAsync(new() { Context = ContextFor(workerId), NodeId = rootId });

		result.HomeNodeId.Should().Be(rootId);
	}

	[Fact]
	public async Task Setting_home_node_to_a_leaf_throws_invariant_violation()
	{
		var (administratorId, workerId, rootId) = await SeedAdministratorAndWorkerWithRootAsync();
		var branchId = await SeedJobNodeAsync(rootId, administratorId);
		var leafId = await SeedJobNodeAsync(branchId, administratorId);
		var sut = CreateSut();

		var act = () => sut.SetHomeNodeAsync(new() { Context = ContextFor(workerId), NodeId = leafId });

		await act.Should().ThrowAsync<InvariantViolationException>()
			.Where(ex => ex.ConstraintId == "home-node-must-not-be-leaf");
	}

	[Fact]
	public async Task Setting_home_node_to_a_nonexistent_node_throws_not_found()
	{
		var (_, workerId, rootId) = await SeedAdministratorAndWorkerWithRootAsync();
		var sut = CreateSut();

		var act = () => sut.SetHomeNodeAsync(new() { Context = ContextFor(workerId), NodeId = new JobNodeId(rootId.Value + 999) });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Setting_home_node_to_null_resets_it_to_root()
	{
		var (administratorId, workerId, rootId) = await SeedAdministratorAndWorkerWithRootAsync();
		var branchId = await SeedJobNodeAsync(rootId, administratorId);
		_ = await SeedJobNodeAsync(branchId, administratorId);
		var sut = CreateSut();
		_ = await sut.SetHomeNodeAsync(new() { Context = ContextFor(workerId), NodeId = branchId });

		var result = await sut.SetHomeNodeAsync(new() { Context = ContextFor(workerId), NodeId = null });

		result.HomeNodeId.Should().BeNull();
		(await GetHomeNodeIdAsync(workerId)).Should().BeNull();
	}

	protected abstract object EncodeInstant(DateTimeOffset value);

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	internal abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	internal abstract IEmployeeCommandPort CreateCommandPort(string connectionString);

	/// <summary>
	///     Seeds a deployed schema, an administrator via the real bootstrap port (which
	///     itself grants <see cref="EmployeeRole.Administrator" />), and one
	///     <see cref="EmployeeRole.Worker" /> employee.
	/// </summary>
	private async Task<(AppUserId AdministratorId, AppUserId WorkerId)> SeedAdministratorAndWorkerAsync()
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

		var workerId = await SeedEmployeeAsync("Grace Hopper", "grace.hopper.employee-roles", EmployeeRole.Worker);

		return (result.AdministratorId, workerId);
	}

	/// <summary>
	///     Same seeding as <see cref="SeedAdministratorAndWorkerAsync" />, plus the permanent
	///     root's id -- only the home-node tests need a job node tree to seed children under.
	/// </summary>
	private async Task<(AppUserId AdministratorId, AppUserId WorkerId, JobNodeId RootId)> SeedAdministratorAndWorkerWithRootAsync()
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

		var workerId = await SeedEmployeeAsync("Grace Hopper", "grace.hopper.home-node", EmployeeRole.Worker);

		return (result.AdministratorId, workerId, result.RootJobNodeId);
	}

	private async Task<JobNodeId> SeedJobNodeAsync(JobNodeId parentId, AppUserId postedByUserId)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
							  VALUES (@parentId, 'Home node test node', @postedByUserId, @postedByUserId, @priorityId, @postedAt)
							  RETURNING id;
							  """;
		AddParameter(command, "@parentId", parentId.Value);
		AddParameter(command, "@postedByUserId", postedByUserId.Value);
		AddParameter(command, "@priorityId", PriorityMedium);
		AddParameter(command, "@postedAt", EncodeInstant(DateTimeOffset.UtcNow));

		return new(Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
	}

	private async Task<long?> GetHomeNodeIdAsync(AppUserId appUserId)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT home_node_id FROM app_user WHERE id = @appUserId;";
		AddParameter(command, "@appUserId", appUserId.Value);

		var value = await command.ExecuteScalarAsync();
		return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
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

		await using var roleCommand = connection.CreateCommand();
		roleCommand.CommandText = """
								  INSERT INTO identity_user_role (identity_user_id, identity_role_id)
								  SELECT id, @roleId FROM identity_user WHERE app_user_id = @appUserId;
								  """;
		AddParameter(roleCommand, "@appUserId", appUserId.Value);
		AddParameter(roleCommand, "@roleId", (short)role);
		_ = await roleCommand.ExecuteNonQueryAsync();

		return appUserId;
	}

	private async Task<string> GetUserNameAsync(AppUserId appUserId)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT user_name FROM identity_user WHERE app_user_id = @appUserId;";
		AddParameter(command, "@appUserId", appUserId.Value);

		return (string)(await command.ExecuteScalarAsync())!;
	}

	private async Task<string> GetIanaTimeZoneAsync(AppUserId appUserId)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT iana_time_zone FROM app_user WHERE id = @appUserId;";
		AddParameter(command, "@appUserId", appUserId.Value);

		return (string)(await command.ExecuteScalarAsync())!;
	}

	private async Task<decimal> GetDefaultHourlyRateAsync(AppUserId appUserId)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT default_hourly_rate FROM app_user WHERE id = @appUserId;";
		AddParameter(command, "@appUserId", appUserId.Value);

		var value = await command.ExecuteScalarAsync();
		return value switch {
			decimal amount => amount,
			string amount => decimal.Parse(amount, CultureInfo.InvariantCulture),
			_ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
		};
	}

	private async Task<ScheduleSummary> GetOnlyScheduleAsync(AppUserId appUserId)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT sv.effective_start, sv.effective_end, sv.iana_time_zone,
							         si.day_of_week, si.start_time, si.end_time, si.crosses_midnight
							  FROM user_schedule_version sv
							  JOIN user_schedule_interval si ON si.schedule_version_id = sv.id
							  WHERE sv.user_id = @appUserId
							  ORDER BY si.day_of_week;
							  """;
		AddParameter(command, "@appUserId", appUserId.Value);

		await using var reader = await command.ExecuteReaderAsync();
		var intervals = new List<ScheduleIntervalSummary>();
		string? effectiveStart = null;
		string? effectiveEnd = null;
		string? ianaTimeZone = null;
		while (await reader.ReadAsync()) {
			effectiveStart ??= NormalizeDate(reader.GetValue(0));
			effectiveEnd ??= reader.IsDBNull(1) ? null : NormalizeDate(reader.GetValue(1));
			ianaTimeZone ??= reader.GetString(2);
			intervals.Add(new(
				Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
				NormalizeTime(reader.GetValue(4)),
				NormalizeTime(reader.GetValue(5)),
				Convert.ToBoolean(reader.GetValue(6), CultureInfo.InvariantCulture)));
		}

		return new(
			effectiveStart ?? throw new InvalidOperationException("No schedule version was found."),
			effectiveEnd,
			ianaTimeZone ?? throw new InvalidOperationException("No schedule version was found."),
			intervals);
	}

	private static string NormalizeDate(object value) => value switch {
		DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
		DateTime dateTime => DateOnly.FromDateTime(dateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
		string text => text,
		_ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
	};

	private static string NormalizeTime(object value) => value switch {
		LocalTime localTime => FormatTime(localTime.Hour, localTime.Minute, localTime.Second),
		TimeOnly timeOnly => FormatTime(timeOnly.Hour, timeOnly.Minute, timeOnly.Second),
		TimeSpan timeSpan => timeSpan.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
		long ticks => LocalTime.FromTicksSinceMidnight(ticks).ToString("HH:mm:ss", null),
		_ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
	};

	private static string FormatTime(int hour, int minute, int second) =>
		$"{hour:D2}:{minute:D2}:{second:D2}";

	private async Task<string> GetSecurityStampAsync(AppUserId appUserId)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT security_stamp FROM identity_user WHERE app_user_id = @appUserId;";
		AddParameter(command, "@appUserId", appUserId.Value);

		return (string)(await command.ExecuteScalarAsync())!;
	}

	/// <summary>
	///     Seeds two-factor state directly via SQL (ADR 0037): the command port under test only
	///     clears this state, so there is no command-port write path to seed it through instead.
	/// </summary>
	private async Task SeedTwoFactorEnabledAsync(AppUserId appUserId)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText =
			"UPDATE identity_user SET two_factor_enabled = @enabled, authenticator_key_protected = @key WHERE app_user_id = @appUserId;";
		AddParameter(command, "@enabled", true);
		AddParameter(command, "@key", new byte[] { 1, 2, 3 });
		AddParameter(command, "@appUserId", appUserId.Value);

		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task<(bool Enabled, byte[]? KeyProtected)> GetTwoFactorStateAsync(AppUserId appUserId)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText =
			"SELECT two_factor_enabled, authenticator_key_protected FROM identity_user WHERE app_user_id = @appUserId;";
		AddParameter(command, "@appUserId", appUserId.Value);

		await using var reader = await command.ExecuteReaderAsync();
		_ = await reader.ReadAsync();
		var enabled = reader.GetBoolean(0);
		var keyProtected = reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1);

		return (enabled, keyProtected);
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

	private sealed class ScheduleSummary(
		string effectiveStart,
		string? effectiveEnd,
		string ianaTimeZone,
		IReadOnlyList<ScheduleIntervalSummary> intervals)
	{
		public string EffectiveStart { get; } = effectiveStart;

		public string? EffectiveEnd { get; } = effectiveEnd;

		public string IanaTimeZone { get; } = ianaTimeZone;

		public IReadOnlyList<ScheduleIntervalSummary> Intervals { get; } = intervals;
	}

	private sealed record ScheduleIntervalSummary(int DayOfWeek, string StartTime, string EndTime, bool CrossesMidnight);
}
