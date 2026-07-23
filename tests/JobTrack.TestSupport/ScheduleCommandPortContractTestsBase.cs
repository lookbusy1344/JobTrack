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
///     Shared contract for <see cref="IScheduleCommandPort" /> (impl plan §7.4 step 3, §7.3 slice 8: add
///     schedule versions and exceptions), asserted identically against PostgreSQL and SQLite by one
///     thin sealed subclass per provider's own test project -- same shape as
///     <see cref="AchievementCommandPortContractTestsBase" />. Mirrors <c>ScheduleCommandsTests</c>'
///     scenarios against the fake port, so the real persistence implementations are held to the same
///     behavioural contract.
/// </summary>
public abstract class ScheduleCommandPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected ScheduleCommandPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task A_worker_can_add_their_own_schedule_version()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var port = CreateSchedulePort(database.ConnectionString);

		var result = await port.AddScheduleVersionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Schedule = CreateWeekdayScheduleVersion(new(2026, 1, 1)),
		});

		result.UserId.Should().Be(workerId);
		result.Version.Should().Be(1);
	}

	[Fact]
	public async Task Adding_a_schedule_version_writes_an_audit_event()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var port = CreateSchedulePort(database.ConnectionString);
		var result = await port.AddScheduleVersionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Schedule = CreateWeekdayScheduleVersion(new(2026, 1, 1)),
		});

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() { EntityType = "user_schedule_version", EntityId = result.Id.Value }, null, AuditSearchTestDefaults.AllRowsLimit);

		audit.Events.Should().ContainSingle();
		audit.Events[0].Operation.Should().Be("add-schedule-version");
		audit.Events[0].ActorId.Should().Be(workerId);
	}

	[Fact]
	public async Task An_administrator_can_add_a_schedule_version_for_another_employee()
	{
		var (administratorId, workerId) = await SeedAdministratorAndWorkerAsync();
		var port = CreateSchedulePort(database.ConnectionString);

		var result = await port.AddScheduleVersionAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Schedule = CreateWeekdayScheduleVersion(new(2026, 1, 1)),
		});

		result.UserId.Should().Be(workerId);
	}

	[Fact]
	public async Task Adding_a_schedule_version_persists_the_canonical_zone_id()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var port = CreateSchedulePort(database.ConnectionString);

		var result = await port.AddScheduleVersionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Schedule = new(
				DateTimeZoneProviders.Tzdb["Asia/Calcutta"],
				new(2026, 1, 1),
				null,
				[new(IsoDayOfWeek.Monday, new(9, 0), new(17, 0))]),
		});

		var storedZoneId = await ReadScheduleVersionZoneIdAsync(result.Id);

		result.Schedule.Zone.Id.Should().Be("Asia/Kolkata");
		storedZoneId.Should().Be("Asia/Kolkata");
	}

	[Fact]
	public async Task A_worker_cannot_add_a_schedule_version_for_another_employee()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.schedule", EmployeeRole.Worker);
		var port = CreateSchedulePort(database.ConnectionString);

		var act = () => port.AddScheduleVersionAsync(new() {
			Context = ContextFor(otherWorkerId),
			UserId = workerId,
			Schedule = CreateWeekdayScheduleVersion(new(2026, 1, 1)),
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Adding_a_schedule_version_for_a_nonexistent_employee_throws_not_found()
	{
		var (administratorId, _) = await SeedAdministratorAndWorkerAsync();
		var port = CreateSchedulePort(database.ConnectionString);

		var act = () => port.AddScheduleVersionAsync(new() {
			Context = ContextFor(administratorId),
			UserId = new(administratorId.Value + 999),
			Schedule = CreateWeekdayScheduleVersion(new(2026, 1, 1)),
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Overlapping_schedule_versions_for_the_same_employee_throw_an_invariant_violation()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var port = CreateSchedulePort(database.ConnectionString);
		_ = await port.AddScheduleVersionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Schedule = CreateWeekdayScheduleVersion(new(2026, 1, 1), new LocalDate(2026, 6, 1)),
		});

		var act = () => port.AddScheduleVersionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Schedule = CreateWeekdayScheduleVersion(new(2026, 3, 1)),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("schedule-version-overlap");
	}

	[Fact]
	public async Task A_worker_can_add_their_own_schedule_exception()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var port = CreateSchedulePort(database.ConnectionString);
		var entry = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.RemoveWorkingTime,
			new(Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 1, 2, 0, 0)),
			null);

		var result = await port.AddScheduleExceptionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Entry = entry,
			Reason = "Public holiday",
		});

		result.UserId.Should().Be(workerId);
		result.Entry.Effect.Should().Be(ScheduleExceptionEffect.RemoveWorkingTime);
	}

	[Fact]
	public async Task Adding_an_identical_schedule_exception_twice_throws_an_invariant_violation()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var port = CreateSchedulePort(database.ConnectionString);
		var entry = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.RemoveWorkingTime,
			new(Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 1, 2, 0, 0)),
			null);
		var request = new AddScheduleExceptionRequest {
			Context = ContextFor(workerId),
			UserId = workerId,
			Entry = entry,
			Reason = "Public holiday",
		};
		_ = await port.AddScheduleExceptionAsync(request);

		var act = () => port.AddScheduleExceptionAsync(request);

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("schedule-exception-already-exists");
	}

	[Fact]
	public async Task A_worker_cannot_add_a_schedule_exception_for_another_employee()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.schedule.exception", EmployeeRole.Worker);
		var port = CreateSchedulePort(database.ConnectionString);
		var entry = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.RemoveWorkingTime,
			new(Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 1, 2, 0, 0)),
			null);

		var act = () => port.AddScheduleExceptionAsync(new() {
			Context = ContextFor(otherWorkerId),
			UserId = workerId,
			Entry = entry,
			Reason = "Public holiday",
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Overlapping_priced_additive_exceptions_for_the_same_employee_throw_an_invariant_violation()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var port = CreateSchedulePort(database.ConnectionString);
		var firstEntry = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.AddWorkingTime,
			new(Instant.FromUtc(2026, 1, 1, 18, 0), Instant.FromUtc(2026, 1, 1, 22, 0)),
			new HourlyRate(30m));
		_ = await port.AddScheduleExceptionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Entry = firstEntry,
			Reason = "Overtime shift",
		});
		var overlappingEntry = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.AddWorkingTime,
			new(Instant.FromUtc(2026, 1, 1, 20, 0), Instant.FromUtc(2026, 1, 1, 23, 0)),
			new HourlyRate(35m));

		var act = () => port.AddScheduleExceptionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Entry = overlappingEntry,
			Reason = "Second overtime shift",
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("schedule-exception-priced-additive-overlap");
	}

	[Fact]
	public async Task Overlapping_unpriced_additive_exceptions_are_allowed()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var port = CreateSchedulePort(database.ConnectionString);
		var firstEntry = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.AddWorkingTime,
			new(Instant.FromUtc(2026, 1, 1, 18, 0), Instant.FromUtc(2026, 1, 1, 22, 0)),
			null);
		_ = await port.AddScheduleExceptionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Entry = firstEntry,
			Reason = "Overtime shift",
		});
		var overlappingEntry = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.AddWorkingTime,
			new(Instant.FromUtc(2026, 1, 1, 20, 0), Instant.FromUtc(2026, 1, 1, 23, 0)),
			null);

		var result = await port.AddScheduleExceptionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Entry = overlappingEntry,
			Reason = "Second overtime shift",
		});

		result.Entry.Should().Be(overlappingEntry);
	}

	[Fact]
	public async Task Correcting_a_schedule_version_replaces_its_effective_range()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var port = CreateSchedulePort(database.ConnectionString);
		var added = await port.AddScheduleVersionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Schedule = CreateWeekdayScheduleVersion(new(2026, 1, 1)),
		});
		var correctedSchedule = CreateWeekdayScheduleVersion(new(2026, 2, 1));

		var result = await port.CorrectScheduleVersionAsync(new() {
			Context = ContextFor(workerId),
			VersionId = added.Id,
			UserId = workerId,
			Version = added.Version,
			Reason = "Fixed a typo in the start date",
			Schedule = correctedSchedule,
		});

		result.Schedule.EffectiveStart.Should().Be(new(2026, 2, 1));
		result.Version.Should().Be(added.Version + 1);
	}

	[Fact]
	public async Task Correcting_a_schedule_version_writes_an_audit_event()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var port = CreateSchedulePort(database.ConnectionString);
		var added = await port.AddScheduleVersionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Schedule = CreateWeekdayScheduleVersion(new(2026, 1, 1)),
		});

		_ = await port.CorrectScheduleVersionAsync(new() {
			Context = ContextFor(workerId),
			VersionId = added.Id,
			UserId = workerId,
			Version = added.Version,
			Reason = "Fixed a typo in the start date",
			Schedule = CreateWeekdayScheduleVersion(new(2026, 2, 1)),
		});

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() { EntityType = "user_schedule_version", EntityId = added.Id.Value }, null, AuditSearchTestDefaults.AllRowsLimit);

		audit.Events.Should().Contain(e => e.Operation == "correct-schedule-version" && e.ActorId == workerId);
	}

	[Fact]
	public async Task Correcting_a_schedule_version_with_a_stale_version_throws_a_concurrency_conflict()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var port = CreateSchedulePort(database.ConnectionString);
		var added = await port.AddScheduleVersionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Schedule = CreateWeekdayScheduleVersion(new(2026, 1, 1)),
		});

		var act = () => port.CorrectScheduleVersionAsync(new() {
			Context = ContextFor(workerId),
			VersionId = added.Id,
			UserId = workerId,
			Version = added.Version + 1,
			Reason = "Fixed a typo in the start date",
			Schedule = CreateWeekdayScheduleVersion(new(2026, 2, 1)),
		});

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	[Fact]
	public async Task Correcting_a_schedule_version_into_overlap_with_another_throws_an_invariant_violation()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var port = CreateSchedulePort(database.ConnectionString);
		_ = await port.AddScheduleVersionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Schedule = CreateWeekdayScheduleVersion(new(2026, 1, 1), new LocalDate(2026, 3, 1)),
		});
		var toCorrect = await port.AddScheduleVersionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Schedule = CreateWeekdayScheduleVersion(new(2026, 6, 1)),
		});

		var act = () => port.CorrectScheduleVersionAsync(new() {
			Context = ContextFor(workerId),
			VersionId = toCorrect.Id,
			UserId = workerId,
			Version = toCorrect.Version,
			Reason = "Moving the start date earlier",
			Schedule = CreateWeekdayScheduleVersion(new(2026, 2, 1)),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("schedule-version-overlap");
	}

	[Fact]
	public async Task A_worker_cannot_correct_another_employees_schedule_version()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.correct-version", EmployeeRole.Worker);
		var port = CreateSchedulePort(database.ConnectionString);
		var added = await port.AddScheduleVersionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Schedule = CreateWeekdayScheduleVersion(new(2026, 1, 1)),
		});

		var act = () => port.CorrectScheduleVersionAsync(new() {
			Context = ContextFor(otherWorkerId),
			VersionId = added.Id,
			UserId = workerId,
			Version = added.Version,
			Reason = "Attempted correction",
			Schedule = CreateWeekdayScheduleVersion(new(2026, 2, 1)),
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Correcting_a_schedule_exception_replaces_its_interval_and_reason()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var port = CreateSchedulePort(database.ConnectionString);
		var added = await port.AddScheduleExceptionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Entry = new(
				ScheduleExceptionEffect.RemoveWorkingTime,
				new(Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 1, 2, 0, 0)),
				null),
			Reason = "Public holiday",
		});
		var correctedEntry = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.RemoveWorkingTime,
			new(Instant.FromUtc(2026, 1, 3, 0, 0), Instant.FromUtc(2026, 1, 4, 0, 0)),
			null);

		var result = await port.CorrectScheduleExceptionAsync(new() {
			Context = ContextFor(workerId),
			ExceptionId = added.Id,
			UserId = workerId,
			Version = added.Version,
			Reason = "Wrong date entered originally",
			Entry = correctedEntry,
		});

		result.Entry.Should().Be(correctedEntry);
		result.Reason.Should().Be("Wrong date entered originally");
		result.Version.Should().Be(added.Version + 1);
	}

	[Fact]
	public async Task Correcting_a_schedule_exception_writes_an_audit_event()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var port = CreateSchedulePort(database.ConnectionString);
		var added = await port.AddScheduleExceptionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Entry = new(
				ScheduleExceptionEffect.RemoveWorkingTime,
				new(Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 1, 2, 0, 0)),
				null),
			Reason = "Public holiday",
		});

		_ = await port.CorrectScheduleExceptionAsync(new() {
			Context = ContextFor(workerId),
			ExceptionId = added.Id,
			UserId = workerId,
			Version = added.Version,
			Reason = "Wrong date entered originally",
			Entry = new(
				ScheduleExceptionEffect.RemoveWorkingTime,
				new(Instant.FromUtc(2026, 1, 3, 0, 0), Instant.FromUtc(2026, 1, 4, 0, 0)),
				null),
		});

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() { EntityType = "user_schedule_exception", EntityId = added.Id.Value }, null, AuditSearchTestDefaults.AllRowsLimit);

		audit.Events.Should().Contain(e => e.Operation == "correct-schedule-exception" && e.ActorId == workerId);
	}

	[Fact]
	public async Task Correcting_a_schedule_exception_with_a_stale_version_throws_a_concurrency_conflict()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var port = CreateSchedulePort(database.ConnectionString);
		var added = await port.AddScheduleExceptionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Entry = new(
				ScheduleExceptionEffect.RemoveWorkingTime,
				new(Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 1, 2, 0, 0)),
				null),
			Reason = "Public holiday",
		});

		var act = () => port.CorrectScheduleExceptionAsync(new() {
			Context = ContextFor(workerId),
			ExceptionId = added.Id,
			UserId = workerId,
			Version = added.Version + 1,
			Reason = "Wrong date entered originally",
			Entry = new(
				ScheduleExceptionEffect.RemoveWorkingTime,
				new(Instant.FromUtc(2026, 1, 3, 0, 0), Instant.FromUtc(2026, 1, 4, 0, 0)),
				null),
		});

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	[Fact]
	public async Task Correcting_a_schedule_exception_into_overlap_with_another_priced_exception_throws_an_invariant_violation()
	{
		var (_, workerId) = await SeedAdministratorAndWorkerAsync();
		var port = CreateSchedulePort(database.ConnectionString);
		_ = await port.AddScheduleExceptionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Entry = new(
				ScheduleExceptionEffect.AddWorkingTime,
				new(Instant.FromUtc(2026, 1, 1, 18, 0), Instant.FromUtc(2026, 1, 1, 22, 0)),
				new HourlyRate(30m)),
			Reason = "Overtime shift",
		});
		var toCorrect = await port.AddScheduleExceptionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Entry = new(
				ScheduleExceptionEffect.AddWorkingTime,
				new(Instant.FromUtc(2026, 1, 5, 18, 0), Instant.FromUtc(2026, 1, 5, 22, 0)),
				new HourlyRate(30m)),
			Reason = "Second overtime shift",
		});

		var act = () => port.CorrectScheduleExceptionAsync(new() {
			Context = ContextFor(workerId),
			ExceptionId = toCorrect.Id,
			UserId = workerId,
			Version = toCorrect.Version,
			Reason = "Moved to overlap the first shift",
			Entry = new(
				ScheduleExceptionEffect.AddWorkingTime,
				new(Instant.FromUtc(2026, 1, 1, 20, 0), Instant.FromUtc(2026, 1, 1, 23, 0)),
				new HourlyRate(35m)),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("schedule-exception-priced-additive-overlap");
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	internal abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	internal abstract IScheduleCommandPort CreateSchedulePort(string connectionString);

	internal abstract IAuditQueryPort CreateAuditQueryPort(string connectionString);

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	private static ScheduleVersion CreateWeekdayScheduleVersion(LocalDate effectiveStart, LocalDate? effectiveEnd = null) =>
		new(
			DateTimeZoneProviders.Tzdb["Europe/London"],
			effectiveStart,
			effectiveEnd,
			[new(IsoDayOfWeek.Monday, new(9, 0), new(17, 0))]);

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

		var workerId = await SeedEmployeeAsync("Grace Hopper", "grace.hopper.schedule", EmployeeRole.Worker);

		return (result.AdministratorId, workerId);
	}

	private async Task<string> ReadScheduleVersionZoneIdAsync(ScheduleVersionId scheduleVersionId)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT iana_time_zone FROM user_schedule_version WHERE id = @scheduleVersionId;";
		AddParameter(command, "@scheduleVersionId", scheduleVersionId.Value);
		return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!;
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
