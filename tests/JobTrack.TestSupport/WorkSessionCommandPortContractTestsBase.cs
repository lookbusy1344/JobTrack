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
///     Shared contract for <see cref="IWorkSessionCommandPort" /> (impl plan §7.4 step 3, §7.3 slice 6:
///     start, finish, resume, and correct work sessions), asserted identically against PostgreSQL and
///     SQLite by one thin sealed subclass per provider's own test project -- same shape as
///     <see cref="JobNodeCommandPortContractTestsBase" />. Mirrors <c>WorkCommandsTests</c>' scenarios
///     against the fake port, so the real persistence implementations are held to the same
///     behavioural contract.
/// </summary>
public abstract class WorkSessionCommandPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected WorkSessionCommandPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	/// <summary>
	///     Exposed so a provider-specific subclass can add its own concurrency/race tests
	///     (plan §6) that need to open additional ports/connections against the same database.
	/// </summary>
	protected string ConnectionString => database.ConnectionString;

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task A_worker_can_start_a_session_for_their_own_work()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);

		var result = await port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = workerId });

		result.LeafWorkId.Should().Be(leafId);
		result.WorkedByUserId.Should().Be(workerId);
		result.FinishedAt.Should().BeNull();
		result.Version.Should().Be(1);
	}

	[Fact]
	public async Task Starting_a_session_writes_an_audit_event()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var result = await port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = workerId });

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() { EntityType = "work_session", EntityId = result.Id.Value }, null, AuditSearchTestDefaults.AllRowsLimit);

		audit.Events.Should().ContainSingle();
		audit.Events[0].Operation.Should().Be("start-work-session");
		audit.Events[0].ActorId.Should().Be(workerId);
	}

	[Fact]
	public async Task Starting_a_session_uses_one_clock_instant_for_the_entity_and_audit_event()
	{
		var operationInstant = Instant.FromUtc(2026, 7, 20, 12, 34, 56);
		var clock = new AdjustableClock(operationInstant);
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString, clock);

		var result = await port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = workerId });
		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() { EntityType = "work_session", EntityId = result.Id.Value }, null, AuditSearchTestDefaults.AllRowsLimit);

		clock.ReadCount.Should().Be(1);
		result.StartedAt.Should().Be(operationInstant);
		audit.Events.Should().ContainSingle().Which.OccurredAt.Should().Be(operationInstant);
	}

	[Fact]
	public async Task A_worker_cannot_start_a_session_for_another_worker()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.session", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.StartSessionAsync(new() { Context = ContextFor(otherWorkerId), LeafWorkId = leafId, WorkedByUserId = workerId });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task The_owner_of_a_leaf_can_start_a_session_on_behalf_of_another_worker()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.onbehalf", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);

		var result = await port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = otherWorkerId });

		result.WorkedByUserId.Should().Be(otherWorkerId);
	}

	[Fact]
	public async Task A_worker_who_does_not_control_the_leaf_cannot_start_even_their_own_session()
	{
		var (_, _, _, leafId) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.ownsession", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.StartSessionAsync(new() { Context = ContextFor(otherWorkerId), LeafWorkId = leafId, WorkedByUserId = otherWorkerId });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task A_worker_cannot_start_a_session_on_an_unassigned_leaf()
	{
		var (rootId, jobManagerId, workerId, _) = await SeedReadyLeafAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var unassignedLeaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Description = "Unassigned pool leaf",
			OwnerUserId = null,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = unassignedLeaf.Id });
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = unassignedLeaf.Id, WorkedByUserId = workerId });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task An_administrator_can_start_a_session_on_an_unassigned_leaf()
	{
		var (rootId, jobManagerId, workerId, _) = await SeedReadyLeafAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var unassignedLeaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Description = "Unassigned pool leaf",
			OwnerUserId = null,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = unassignedLeaf.Id });
		var port = CreateSessionPort(database.ConnectionString);

		var result = await port.StartSessionAsync(new() {
			Context = ContextFor(jobManagerId),
			LeafWorkId = unassignedLeaf.Id,
			WorkedByUserId = workerId,
		});

		result.WorkedByUserId.Should().Be(workerId);
	}

	[Fact]
	public async Task A_worker_who_owns_an_ancestor_branch_can_start_a_session_on_a_descendant_leaf()
	{
		var (rootId, jobManagerId, _, _) = await SeedReadyLeafAsync();
		var branchOwnerId = await SeedEmployeeAsync("Branch Owner", "branch.owner.session", EmployeeRole.Worker);
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var branch = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Description = "Owned branch",
			OwnerUserId = branchOwnerId,
			Priority = Priority.Medium,
		});
		var descendantLeaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(branchOwnerId),
			ParentId = branch.Id,
			Description = "Descendant leaf",
			OwnerUserId = null,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(branchOwnerId), JobNodeId = descendantLeaf.Id });
		var port = CreateSessionPort(database.ConnectionString);

		var result = await port.StartSessionAsync(new() {
			Context = ContextFor(branchOwnerId),
			LeafWorkId = descendantLeaf.Id,
			WorkedByUserId = branchOwnerId,
		});

		result.WorkedByUserId.Should().Be(branchOwnerId);
	}

	[Fact]
	public async Task The_owner_of_a_leaf_can_finish_a_session_they_did_not_record()
	{
		var (rootId, jobManagerId, workerId, _) = await SeedReadyLeafAsync();
		var ownerId = await SeedEmployeeAsync("Controlling Owner", "controlling.owner.finish", EmployeeRole.Worker);
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var leaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Description = "Owner-managed leaf",
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() { Context = ContextFor(jobManagerId), JobNodeId = leaf.Id });
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartSessionAsync(new() { Context = ContextFor(ownerId), LeafWorkId = leaf.Id, WorkedByUserId = workerId });

		var result = await port.FinishSessionAsync(
			new() { Context = ContextFor(ownerId), SessionId = session.Id, Version = session.Version });

		result.FinishedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task Starting_a_session_on_a_leaf_with_no_leaf_work_throws_not_found()
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
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = bareLeaf.Id, WorkedByUserId = workerId });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Starting_a_second_active_session_for_the_same_worker_and_leaf_throws_an_invariant_violation()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		_ = await port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = workerId });

		var act = () => port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = workerId });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-already-active");
	}

	[Fact]
	public async Task Starting_a_session_blocked_by_an_unsatisfied_prerequisite_throws_prerequisite_blocked()
	{
		var (rootId, jobManagerId, workerId, requiredLeaf) = await SeedReadyLeafAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var dependentLeaf = await CreateReadyLeafAsync(jobNodePort, rootId, jobManagerId, workerId);
		await jobNodePort.AddPrerequisiteAsync(new() {
			Context = ContextFor(jobManagerId),
			RequiredJobId = requiredLeaf,
			DependentJobId = dependentLeaf,
		});
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = dependentLeaf, WorkedByUserId = workerId });

		await act.Should().ThrowAsync<PrerequisiteBlockedException>();
	}

	[Fact]
	public async Task A_worker_can_start_a_session_with_a_backdated_start_instant()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var backdatedStart = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromHours(2));

		var result = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
			StartedAt = backdatedStart,
		});

		result.StartedAt.Should().Be(backdatedStart);
	}

	[Fact]
	public async Task Starting_a_session_with_a_future_start_instant_throws_an_invariant_violation()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var futureStart = SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromHours(2));

		var act = () => port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
			StartedAt = futureStart,
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-start-in-future");
	}

	[Fact]
	public async Task Starting_a_session_with_a_backdated_start_that_overlaps_a_finished_session_throws_an_invariant_violation()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var firstStart = Instant.FromUtc(2026, 1, 1, 9, 0);
		var firstFinish = Instant.FromUtc(2026, 1, 1, 10, 0);
		var first = await port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = workerId });
		first = await port.FinishSessionAsync(
			new() { Context = ContextFor(workerId), SessionId = first.Id, Version = first.Version });
		_ = await port.CorrectSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = first.Id,
			StartedAt = firstStart,
			FinishedAt = firstFinish,
			Reason = "Establish a fixed historical interval",
			Version = first.Version,
		});

		var act = () => port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
			StartedAt = firstStart.Plus(Duration.FromMinutes(30)),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-overlap");
	}

	[Fact]
	public async Task Finishing_a_session_sets_finished_at_and_bumps_the_version()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = workerId });

		var result = await port.FinishSessionAsync(
			new() { Context = ContextFor(workerId), SessionId = session.Id, Version = session.Version });

		result.FinishedAt.Should().NotBeNull();
		result.Version.Should().Be(2);
	}

	[Fact]
	public async Task A_worker_cannot_finish_another_workers_session()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.finish", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = workerId });

		var act = () => port.FinishSessionAsync(new() { Context = ContextFor(otherWorkerId), SessionId = session.Id, Version = session.Version });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Finishing_with_a_stale_version_throws_a_concurrency_conflict()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = workerId });

		var act = () => port.FinishSessionAsync(new() { Context = ContextFor(workerId), SessionId = session.Id, Version = session.Version + 1 });

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	[Fact]
	public async Task Finishing_a_session_with_a_mismatched_expected_leaf_throws_not_found()
	{
		// Remediation plan §3.5: a nested route's parent identifier must actually match the
		// session, or the mismatch is treated identically to a nonexistent session.
		var (rootId, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var otherLeafId = await CreateReadyLeafAsync(jobNodePort, rootId, jobManagerId, workerId);
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = workerId });

		var act = () => port.FinishSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = session.Id,
			Version = session.Version,
			LeafWorkId = otherLeafId,
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task A_worker_can_finish_a_session_with_a_backdated_finish_instant()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var backdatedStart = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromHours(2));
		var session = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
			StartedAt = backdatedStart,
		});
		var backdatedFinish = backdatedStart.Plus(Duration.FromHours(1));

		var result = await port.FinishSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = session.Id,
			Version = session.Version,
			FinishedAt = backdatedFinish,
		});

		result.FinishedAt.Should().Be(backdatedFinish);
	}

	[Fact]
	public async Task Finishing_a_session_with_a_finish_instant_before_its_start_throws_an_invariant_violation()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = workerId });

		var act = () => port.FinishSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = session.Id,
			Version = session.Version,
			FinishedAt = session.StartedAt.Minus(Duration.FromHours(1)),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-invalid-interval");
	}

	[Fact]
	public async Task Finishing_a_session_with_a_future_finish_instant_throws_an_invariant_violation()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = workerId });

		var act = () => port.FinishSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = session.Id,
			Version = session.Version,
			FinishedAt = SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromHours(2)),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-finish-in-future");
	}

	[Fact]
	public async Task Correcting_a_session_replaces_its_interval()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = workerId });
		var correctedStart = session.StartedAt.Minus(Duration.FromHours(1));
		var correctedFinish = session.StartedAt;

		var result = await port.CorrectSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = session.Id,
			StartedAt = correctedStart,
			FinishedAt = correctedFinish,
			Reason = "Forgot to start the timer on time",
			Version = session.Version,
		});

		result.StartedAt.Should().Be(correctedStart);
		result.FinishedAt.Should().Be(correctedFinish);
	}

	[Fact]
	public async Task Correcting_a_session_to_an_invalid_interval_throws_an_invariant_violation()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = workerId });

		var act = () => port.CorrectSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = session.Id,
			StartedAt = session.StartedAt,
			FinishedAt = session.StartedAt.Minus(Duration.FromHours(1)),
			Reason = "Bad correction",
			Version = session.Version,
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-invalid-interval");
	}

	[Fact]
	public async Task Correcting_a_session_into_overlap_with_another_session_throws_an_invariant_violation()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var firstStart = Instant.FromUtc(2026, 1, 1, 9, 0);
		var firstFinish = Instant.FromUtc(2026, 1, 1, 10, 0);
		var first = await port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = workerId });
		first = await port.FinishSessionAsync(
			new() { Context = ContextFor(workerId), SessionId = first.Id, Version = first.Version });
		first = await port.CorrectSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = first.Id,
			StartedAt = firstStart,
			FinishedAt = firstFinish,
			Reason = "Establish a fixed historical interval",
			Version = first.Version,
		});
		var second = await port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = workerId });

		var act = () => port.CorrectSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = second.Id,
			StartedAt = firstStart,
			FinishedAt = null,
			Reason = "Overlapping correction",
			Version = second.Version,
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-overlap");
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	protected abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	protected abstract IJobNodeCommandPort CreateJobNodePort(string connectionString);

	protected abstract IWorkSessionCommandPort CreateSessionPort(string connectionString);

	protected abstract IWorkSessionCommandPort CreateSessionPort(string connectionString, IClock clock);

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
	///     itself assigns no roles), one <see cref="EmployeeRole.Worker" /> employee, and a leaf with
	///     <c>LeafWork</c> attached and ready (no prerequisites). Exposed (rather than private) so a
	///     provider-specific subclass can add its own concurrency/race tests (plan §6) reusing the same
	///     seeding.
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

		var workerId = await SeedEmployeeAsync("Grace Hopper", "grace.hopper.session", EmployeeRole.Worker);

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
