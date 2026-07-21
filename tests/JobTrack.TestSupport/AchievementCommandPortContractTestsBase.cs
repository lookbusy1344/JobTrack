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
///     Shared contract for <see cref="IAchievementCommandPort" /> (impl plan §7.4 step 3, §7.3 slice 7:
///     change achievement subject to prerequisite gates, ADR 0001), asserted identically against
///     PostgreSQL and SQLite by one thin sealed subclass per provider's own test project -- same shape
///     as <see cref="WorkSessionCommandPortContractTestsBase" />. Mirrors <c>WorkCommandsTests</c>'
///     achievement scenarios against the fake port, so the real persistence implementations are held to
///     the same behavioural contract.
/// </summary>
public abstract class AchievementCommandPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected AchievementCommandPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	/// <summary>
	///     Exposed so a provider-specific subclass can add its own concurrency/race tests
	///     (plan §6) that need to open additional ports/connections against the same database.
	/// </summary>
	protected string ConnectionString => database.ConnectionString;

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task A_job_manager_can_transition_a_leaf_from_waiting_to_in_progress()
	{
		var (_, jobManagerId, _, leafId) = await SeedReadyLeafAsync();
		var port = CreateAchievementPort(database.ConnectionString);

		var result = await port.SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.InProgress,
			Reason = "Work has started",
			Version = 1,
		});

		result.Achievement.Should().Be(Achievement.InProgress);
		result.Version.Should().Be(2);
	}

	[Fact]
	public async Task Changing_achievement_writes_an_audit_event()
	{
		var (_, jobManagerId, _, leafId) = await SeedReadyLeafAsync();
		var port = CreateAchievementPort(database.ConnectionString);
		_ = await port.SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.InProgress,
			Reason = "Work has started",
			Version = 1,
		});

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() { EntityType = "leaf_work", EntityId = leafId.Value }, null, AuditSearchTestDefaults.AllRowsLimit);

		// AttachLeafWorkAsync (SeedReadyLeafAsync) also writes a leaf_work audit event, so filter
		// down to the transition this test actually performed.
		var achievementEvent = audit.Events.Should().ContainSingle(e => e.Operation == "set-achievement").Subject;
		achievementEvent.Reason.Should().Be("Work has started");
	}

	[Fact]
	public async Task A_worker_can_transition_a_leaf_they_own_from_waiting_to_in_progress()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateAchievementPort(database.ConnectionString);

		var result = await port.SetAchievementAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.InProgress,
			Reason = "Work has started",
			Version = 1,
		});

		result.Achievement.Should().Be(Achievement.InProgress);
	}

	[Fact]
	public async Task A_worker_cannot_change_achievement_for_a_leaf_they_do_not_own()
	{
		var (rootId, jobManagerId, _, _) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.achievement", EmployeeRole.Worker);
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var leafId = await CreateReadyLeafAsync(jobNodePort, rootId, jobManagerId, jobManagerId);
		var port = CreateAchievementPort(database.ConnectionString);

		var act = () => port.SetAchievementAsync(new() {
			Context = ContextFor(otherWorkerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.InProgress,
			Reason = "Trying to start someone else's work",
			Version = 1,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Changing_achievement_on_a_leaf_with_no_leaf_work_throws_not_found()
	{
		var (rootId, jobManagerId, workerId, _) = await SeedReadyLeafAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var bareLeaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Description = "Bare leaf",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		var port = CreateAchievementPort(database.ConnectionString);

		var act = () => port.SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = bareLeaf.Id,
			NewAchievement = Achievement.InProgress,
			Reason = "irrelevant",
			Version = 1,
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Changing_achievement_with_a_stale_version_throws_a_concurrency_conflict()
	{
		var (_, jobManagerId, _, leafId) = await SeedReadyLeafAsync();
		var port = CreateAchievementPort(database.ConnectionString);

		var act = () => port.SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.InProgress,
			Reason = "Work has started",
			Version = 2,
		});

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	[Fact]
	public async Task An_impermissible_transition_throws_an_invariant_violation()
	{
		var (_, jobManagerId, _, leafId) = await SeedReadyLeafAsync();
		var port = CreateAchievementPort(database.ConnectionString);

		var act = () => port.SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.Success,
			Reason = "Skipping in-progress",
			Version = 1,
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("achievement-transition-not-permitted");
	}

	[Fact]
	public async Task Transitioning_to_success_while_a_prerequisite_is_unsatisfied_throws_prerequisite_blocked()
	{
		var (rootId, jobManagerId, workerId, requiredLeaf) = await SeedReadyLeafAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var dependentLeaf = await CreateReadyLeafAsync(jobNodePort, rootId, jobManagerId, workerId);
		await jobNodePort.AddPrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = requiredLeaf,
			DependentJobId = dependentLeaf,
		});
		var port = CreateAchievementPort(database.ConnectionString);
		_ = await port.SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = dependentLeaf,
			NewAchievement = Achievement.InProgress,
			Reason = "Attempting despite the prerequisite",
			Version = 1,
		});

		var act = () => port.SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = dependentLeaf,
			NewAchievement = Achievement.Success,
			Reason = "Trying to complete anyway",
			Version = 2,
		});

		await act.Should().ThrowAsync<PrerequisiteBlockedException>();
	}

	[Fact]
	public async Task A_worker_may_not_reopen_a_terminal_state_even_for_a_leaf_they_own()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateAchievementPort(database.ConnectionString);
		_ = await port.SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.InProgress,
			Reason = "Starting",
			Version = 1,
		});
		_ = await port.SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.Unsuccessful,
			Reason = "Did not pan out",
			Version = 2,
		});

		var act = () => port.SetAchievementAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.Waiting,
			Reason = "Trying again",
			Version = 3,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task A_job_manager_can_reopen_a_terminal_state()
	{
		var (_, jobManagerId, _, leafId) = await SeedReadyLeafAsync();
		var port = CreateAchievementPort(database.ConnectionString);
		_ = await port.SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.InProgress,
			Reason = "Starting",
			Version = 1,
		});
		_ = await port.SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.Unsuccessful,
			Reason = "Did not pan out",
			Version = 2,
		});

		var result = await port.SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.Waiting,
			Reason = "Trying again",
			Version = 3,
		});

		result.Achievement.Should().Be(Achievement.Waiting);
	}

	[Fact]
	public async Task Transitioning_to_a_terminal_achievement_while_a_session_is_active_is_rejected()
	{
		// ADR 0044: import a leaf carrying an already-open (FinishedAt null) session, then attempt the
		// terminal transition through the public command surface rather than a raw-SQL bypass.
		var (rootId, jobManagerId, workerId, _) = await SeedReadyLeafAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var achievementPort = CreateAchievementPort(database.ConnectionString);
		var now = SystemClock.Instance.GetCurrentInstant();

		var imported = await jobNodePort.ImportSubtreeAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Nodes = [
				new() {
					LocalId = 1,
					Description = "Actively worked leaf",
					OwnerUserId = workerId,
					Priority = Priority.Medium,
					LeafWork = new() {
						WorkedByUserId = workerId, StartedAt = now - Duration.FromHours(1), FinishedAt = null, Achievement = Achievement.InProgress,
					},
				},
			],
		});

		var act = () => achievementPort.SetAchievementAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = imported.Nodes.Single().JobNodeId,
			NewAchievement = Achievement.Success,
			Reason = "Marking done",
			Version = 1,
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("leaf-closure-active-sessions");
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	protected abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	protected abstract IJobNodeCommandPort CreateJobNodePort(string connectionString);

	protected abstract IAchievementCommandPort CreateAchievementPort(string connectionString);

	protected abstract IAuditQueryPort CreateAuditQueryPort(string connectionString);

	protected static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	private static async Task<JobNodeId> CreateReadyLeafAsync(
		IJobNodeCommandPort jobNodePort, JobNodeId parentId, AppUserId jobManagerId, AppUserId workerId)
	{
		var leaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = parentId,
			Description = "Do the thing",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(
			new() { Context = ContextFor(jobManagerId), JobNodeId = leaf.Id });

		return leaf.Id;
	}

	/// <summary>
	///     Seeds a deployed schema, an administrator/root via the real bootstrap port (with the
	///     administrator additionally granted <see cref="EmployeeRole.JobManager" />, since bootstrap
	///     itself assigns no roles), one <see cref="EmployeeRole.Worker" /> employee, and a leaf owned by
	///     that worker with <c>LeafWork</c> attached and ready (no prerequisites). Exposed (rather than
	///     private) so a provider-specific subclass can add its own concurrency/race tests (plan §6)
	///     reusing the same seeding.
	/// </summary>
	protected async Task<(JobNodeId RootId, AppUserId JobManagerId, AppUserId WorkerId, JobNodeId LeafId)> SeedReadyLeafAsync()
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

		await using (var connection = await OpenExistingConnectionAsync()) {
			await AssignRoleAsync(connection, result.AdministratorId, EmployeeRole.JobManager);
		}

		var workerId = await SeedEmployeeAsync("Grace Hopper", "grace.hopper.achievement", EmployeeRole.Worker);

		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var leafId = await CreateReadyLeafAsync(jobNodePort, result.RootJobNodeId, result.AdministratorId, workerId);

		return (result.RootJobNodeId, result.AdministratorId, workerId, leafId);
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
