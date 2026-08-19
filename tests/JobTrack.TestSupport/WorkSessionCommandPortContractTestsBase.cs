namespace JobTrack.TestSupport;

using Abstractions;
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
public abstract partial class WorkSessionCommandPortContractTestsBase : IAsyncLifetime
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

		var result = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});

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
		var result = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() {
				EntityType = "work_session",
				EntityId = result.Id.Value,
			}, null, AuditSearchTestDefaults.AllRowsLimit);

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

		var result = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});
		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() {
				EntityType = "work_session",
				EntityId = result.Id.Value,
			}, null, AuditSearchTestDefaults.AllRowsLimit);

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

		var act = () => port.StartSessionAsync(new() {
			Context = ContextFor(otherWorkerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task The_owner_of_a_leaf_can_start_a_session_on_behalf_of_another_worker()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.onbehalf", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);

		var result = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = otherWorkerId,
		});

		result.WorkedByUserId.Should().Be(otherWorkerId);
	}

	[Fact]
	public async Task A_worker_who_does_not_control_the_leaf_cannot_start_even_their_own_session()
	{
		var (_, _, _, leafId) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.ownsession", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.StartSessionAsync(new() {
			Context = ContextFor(otherWorkerId),
			LeafWorkId = leafId,
			WorkedByUserId = otherWorkerId,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	/// <summary>ADR 0048: starting one's own session on an unassigned leaf claims it, rather than being denied.</summary>
	[Fact]
	public async Task A_worker_starting_a_session_on_an_unassigned_leaf_claims_it_for_the_worked_by_user()
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
		_ = await jobNodePort.AttachLeafWorkAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = unassignedLeaf.Id,
		});
		var port = CreateSessionPort(database.ConnectionString);

		var result = await port.StartSessionAsync(
			new() {
				Context = ContextFor(workerId),
				LeafWorkId = unassignedLeaf.Id,
				WorkedByUserId = workerId,
			});

		result.WorkedByUserId.Should().Be(workerId);
		var reclaim = () => jobNodePort.PickUpAsync(new() {
			Context = ContextFor(workerId),
			NodeId = unassignedLeaf.Id,
		});
		(await reclaim.Should().ThrowAsync<InvariantViolationException>()).Which.ConstraintId.Should().Be("job-node-already-claimed");
	}

	/// <summary>
	///     ADR 0048: a plain Worker starting a session for someone else is unaffected by auto-claim -- the
	///     leaf claims for the target worker, not the actor, so the actor still doesn't control it.
	/// </summary>
	[Fact]
	public async Task A_worker_starting_a_session_for_another_worker_on_an_unassigned_leaf_is_still_denied()
	{
		var (rootId, jobManagerId, workerId, _) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.unassigned", EmployeeRole.Worker);
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		var unassignedLeaf = await jobNodePort.AddChildAsync(new() {
			Context = ContextFor(jobManagerId),
			ParentId = rootId,
			Description = "Unassigned pool leaf",
			OwnerUserId = null,
			Priority = Priority.Medium,
		});
		_ = await jobNodePort.AttachLeafWorkAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = unassignedLeaf.Id,
		});
		var port = CreateSessionPort(database.ConnectionString);

		var act = () =>
			port.StartSessionAsync(new() {
				Context = ContextFor(workerId),
				LeafWorkId = unassignedLeaf.Id,
				WorkedByUserId = otherWorkerId,
			});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	/// <summary>ADR 0048: an administrator's unconditional authority no longer leaves the node unassigned afterward.</summary>
	[Fact]
	public async Task An_administrator_starting_a_session_on_an_unassigned_leaf_also_claims_it_for_the_worked_by_user()
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
		_ = await jobNodePort.AttachLeafWorkAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = unassignedLeaf.Id,
		});
		var port = CreateSessionPort(database.ConnectionString);

		var result = await port.StartSessionAsync(new() {
			Context = ContextFor(jobManagerId),
			LeafWorkId = unassignedLeaf.Id,
			WorkedByUserId = workerId,
		});

		result.WorkedByUserId.Should().Be(workerId);
		var reclaim = () => jobNodePort.PickUpAsync(new() {
			Context = ContextFor(workerId),
			NodeId = unassignedLeaf.Id,
		});
		(await reclaim.Should().ThrowAsync<InvariantViolationException>()).Which.ConstraintId.Should().Be("job-node-already-claimed");
	}

	/// <summary>ADR 0048: the auto-claim writes the same audit action as an explicit pickup, distinguished by its reason.</summary>
	[Fact]
	public async Task Starting_a_session_on_an_unassigned_leaf_writes_an_auto_claim_pickup_audit_event()
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
		_ = await jobNodePort.AttachLeafWorkAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = unassignedLeaf.Id,
		});
		var port = CreateSessionPort(database.ConnectionString);

		_ = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = unassignedLeaf.Id,
			WorkedByUserId = workerId,
		});

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var audit = await auditPort.SearchAuditEventsAsync(
			new() {
				EntityType = "job_node",
				EntityId = unassignedLeaf.Id.Value,
			}, null, AuditSearchTestDefaults.AllRowsLimit);

		var pickup = audit.Events.Should().ContainSingle(e => e.Operation == "pick-up-job-node").Which;
		pickup.ActorId.Should().Be(workerId);
		pickup.Reason.Should().Be("Automatically claimed on session start");
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
		_ = await jobNodePort.AttachLeafWorkAsync(new() {
			Context = ContextFor(branchOwnerId),
			JobNodeId = descendantLeaf.Id,
		});
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
		_ = await jobNodePort.AttachLeafWorkAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leaf.Id,
		});
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartSessionAsync(new() {
			Context = ContextFor(ownerId),
			LeafWorkId = leaf.Id,
			WorkedByUserId = workerId,
		});

		var result = await port.FinishSessionAsync(
			new() {
				Context = ContextFor(ownerId),
				SessionId = session.Id,
				Version = session.Version,
			});

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

		var act = () => port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = bareLeaf.Id,
			WorkedByUserId = workerId,
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Starting_a_second_active_session_for_the_same_worker_and_leaf_throws_an_invariant_violation()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		_ = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});

		var act = () => port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});

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

		var act = () => port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = dependentLeaf,
			WorkedByUserId = workerId,
		});

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
		var first = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});
		first = await port.FinishSessionAsync(
			new() {
				Context = ContextFor(workerId),
				SessionId = first.Id,
				Version = first.Version,
			});
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
	public async Task Starting_a_session_at_the_instant_a_prior_session_finished_does_not_overlap()
	{
		// Session intervals are half-open [start, end) throughout the stack (domain
		// IntervalAlgebra, the Postgres exclusion constraint's tstzrange '[)' bound, and the
		// SQLite triggers): a session ending at exactly 10:00 and a new one starting at exactly
		// 10:00 are adjacent, not overlapping, so this must succeed rather than throw.
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var firstStart = Instant.FromUtc(2026, 1, 1, 9, 0);
		var firstFinish = Instant.FromUtc(2026, 1, 1, 10, 0);
		var first = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});
		first = await port.FinishSessionAsync(
			new() {
				Context = ContextFor(workerId),
				SessionId = first.Id,
				Version = first.Version,
			});
		_ = await port.CorrectSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = first.Id,
			StartedAt = firstStart,
			FinishedAt = firstFinish,
			Reason = "Establish a fixed historical interval",
			Version = first.Version,
		});

		var result = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
			StartedAt = firstFinish,
		});

		result.StartedAt.Should().Be(firstFinish);
	}

	[Fact]
	public async Task Finishing_a_session_sets_finished_at_and_bumps_the_version()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});

		var result = await port.FinishSessionAsync(
			new() {
				Context = ContextFor(workerId),
				SessionId = session.Id,
				Version = session.Version,
			});

		result.FinishedAt.Should().NotBeNull();
		result.Version.Should().Be(2);
	}

	[Fact]
	public async Task A_worker_cannot_finish_another_workers_session()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.finish", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});

		var act = () => port.FinishSessionAsync(new() {
			Context = ContextFor(otherWorkerId),
			SessionId = session.Id,
			Version = session.Version,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Finishing_with_a_stale_version_throws_a_concurrency_conflict()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});

		var act = () => port.FinishSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = session.Id,
			Version = session.Version + 1,
		});

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
		var session = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});

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
		var session = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});

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
		var session = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});

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
		var session = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});
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
		var session = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});

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
		var first = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});
		first = await port.FinishSessionAsync(
			new() {
				Context = ContextFor(workerId),
				SessionId = first.Id,
				Version = first.Version,
			});
		first = await port.CorrectSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = first.Id,
			StartedAt = firstStart,
			FinishedAt = firstFinish,
			Reason = "Establish a fixed historical interval",
			Version = first.Version,
		});
		var second = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});

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

	[Fact]
	public async Task Correcting_a_session_to_start_at_the_instant_another_session_finished_does_not_overlap()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var firstStart = Instant.FromUtc(2026, 1, 1, 9, 0);
		var firstFinish = Instant.FromUtc(2026, 1, 1, 10, 0);
		var first = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});
		first = await port.FinishSessionAsync(
			new() {
				Context = ContextFor(workerId),
				SessionId = first.Id,
				Version = first.Version,
			});
		first = await port.CorrectSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = first.Id,
			StartedAt = firstStart,
			FinishedAt = firstFinish,
			Reason = "Establish a fixed historical interval",
			Version = first.Version,
		});
		var second = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});

		var result = await port.CorrectSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = second.Id,
			StartedAt = firstFinish,
			FinishedAt = null,
			Reason = "Adjacent correction",
			Version = second.Version,
		});

		result.StartedAt.Should().Be(firstFinish);
	}

	[Fact]
	public async Task Starting_a_session_for_a_leaf_with_terminal_achievement_throws_an_invariant_violation()
	{
		// ADR 0044: no achievement port is available in this shared base, so the terminal state is
		// set directly against the schema -- exactly the "direct-write bypass" scenario the database
		// trigger backstops, exercised here through the command's own application-side pre-check.
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		await SetAchievementIdAsync(leafId, (short)Achievement.Success);
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-leaf-closed");
	}

	[Fact]
	public async Task Starting_a_session_for_an_archived_leaf_throws_an_invariant_violation()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		_ = await jobNodePort.ArchiveAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = leafId,
			Version = 1,
		});
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-leaf-closed");
	}

	[Fact]
	public async Task Correcting_a_finished_session_back_to_active_on_a_terminal_leaf_throws_an_invariant_violation()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});
		session = await port.FinishSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = session.Id,
			Version = session.Version,
		});
		await SetAchievementIdAsync(leafId, (short)Achievement.Success);

		var act = () => port.CorrectSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = session.Id,
			StartedAt = session.StartedAt,
			FinishedAt = null,
			Reason = "Reopening by mistake",
			Version = session.Version,
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-leaf-closed");
	}

	[Fact]
	public async Task A_worker_can_finish_their_own_session_after_losing_control_of_the_node()
	{
		var (rootId, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("New Owner", "new.owner.selffinish", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		_ = await jobNodePort.EditAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = leafId,
			Description = "Reassigned away from the session's own worker",
			OwnerUserId = otherWorkerId,
			Priority = Priority.Medium,
			Version = 1,
		});

		var result = await port.FinishSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = session.Id,
			Version = session.Version,
		});

		result.FinishedAt.Should().NotBeNull();
		_ = rootId;
	}

	[Fact]
	public async Task A_bystander_who_never_controlled_the_node_and_did_not_record_the_session_cannot_finish_it()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("New Owner", "new.owner.selffinish2", EmployeeRole.Worker);
		var bystanderId = await SeedEmployeeAsync("Bystander", "bystander.selffinish", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartSessionAsync(new() {
			Context = ContextFor(workerId),
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});
		var jobNodePort = CreateJobNodePort(database.ConnectionString);
		_ = await jobNodePort.EditAsync(new() {
			Context = ContextFor(jobManagerId),
			NodeId = leafId,
			Description = "Reassigned to a different worker",
			OwnerUserId = otherWorkerId,
			Priority = Priority.Medium,
			Version = 1,
		});

		var act = () => port.FinishSessionAsync(new() {
			Context = ContextFor(bystanderId),
			SessionId = session.Id,
			Version = session.Version,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Completing_a_leaf_with_one_active_session_finishes_it_and_records_success()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});

		var result = await port.CompleteLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			Version = 2,
			ExpectedActiveSessions = [
				new() {
					Id = session.Id, Version = session.Version,
				},
			],
		});

		result.Achievement.Should().Be(Achievement.Success);
		result.FinishedSessions.Should().ContainSingle().Which.FinishedAt.Should().NotBeNull();
	}

	[Theory]
	[InlineData(Achievement.Cancelled)]
	[InlineData(Achievement.Unsuccessful)]
	public async Task Completing_a_leaf_with_a_non_success_final_achievement_records_it(Achievement finalAchievement)
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});

		var result = await port.CompleteLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			Version = 2,
			ExpectedActiveSessions = [
				new() {
					Id = session.Id, Version = session.Version,
				},
			],
			FinalAchievement = finalAchievement,
		});

		result.Achievement.Should().Be(finalAchievement);
		result.FinishedSessions.Should().ContainSingle().Which.FinishedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task Completing_a_leaf_with_a_final_achievement_in_progress_cannot_reach_throws_an_invariant_violation()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		_ = await port.StartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});

		var act = () => port.CompleteLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			Version = 2,
			ExpectedActiveSessions = [],
			FinalAchievement = Achievement.Waiting,
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("achievement-transition-not-permitted");
	}

	[Fact]
	public async Task Completing_a_leaf_writes_a_single_correlated_audit_trail_for_the_achievement_and_finished_session()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});

		var result = await port.CompleteLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			Version = 2,
			ExpectedActiveSessions = [
				new() {
					Id = session.Id, Version = session.Version,
				},
			],
		});

		var auditPort = CreateAuditQueryPort(database.ConnectionString);
		var leafAudit = await auditPort.SearchAuditEventsAsync(
			new() {
				EntityType = "leaf_work",
				EntityId = leafId.Value,
			}, null, AuditSearchTestDefaults.AllRowsLimit);
		leafAudit.Events.Should().Contain(e => e.Operation == "set-achievement" && e.Reason == "Completed from the leaf work page");

		var sessionAudit = await auditPort.SearchAuditEventsAsync(
			new() {
				EntityType = "work_session",
				EntityId = session.Id.Value,
			}, null, AuditSearchTestDefaults.AllRowsLimit);
		sessionAudit.Events.Should().Contain(e => e.Operation == "finish-work-session");
		result.Achievement.Should().Be(Achievement.Success);
	}

	[Fact]
	public async Task Completing_a_leaf_with_zero_active_sessions_is_permitted_from_in_progress()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});
		_ = await port.FinishSessionAsync(new() {
			Context = ContextFor(workerId),
			SessionId = session.Id,
			Version = session.Version,
		});

		var result = await port.CompleteLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			Version = 2,
			ExpectedActiveSessions = [],
		});

		result.Achievement.Should().Be(Achievement.Success);
		result.FinishedSessions.Should().BeEmpty();
	}

	[Fact]
	public async Task Completing_a_leaf_still_waiting_throws_an_invariant_violation()
	{
		var (_, jobManagerId, _, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);

		var act = () => port.CompleteLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			Version = 1,
			ExpectedActiveSessions = [],
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("achievement-transition-not-permitted");
	}

	[Fact]
	public async Task A_worker_who_does_not_control_the_leaf_cannot_complete_it()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.complete", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});

		var act = () => port.CompleteLeafAsync(new() {
			Context = ContextFor(otherWorkerId),
			JobNodeId = leafId,
			Version = 2,
			ExpectedActiveSessions = [
				new() {
					Id = session.Id, Version = session.Version,
				},
			],
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Completing_a_leaf_with_a_stale_expected_active_session_set_throws_a_concurrency_conflict()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});

		var act = () => port.CompleteLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			Version = 2,
			ExpectedActiveSessions = [
				new() {
					Id = session.Id, Version = session.Version + 1,
				},
			],
		});

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	[Fact]
	public async Task Completing_a_leaf_finishes_every_confirmed_active_session_for_several_workers_atomically()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.multicomplete", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);
		var first = await port.StartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});
		var second = await port.StartSessionAsync(new() {
			Context = ContextFor(jobManagerId),
			LeafWorkId = leafId,
			WorkedByUserId = otherWorkerId,
		});

		var result = await port.CompleteLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			Version = 2,
			ExpectedActiveSessions = [
				new() {
					Id = first.Id, Version = first.Version,
				},
				new() {
					Id = second.Id, Version = second.Version,
				},
			],
		});

		result.FinishedSessions.Should().HaveCount(2);
		result.FinishedSessions.Should().OnlyContain(s => s.FinishedAt != null);
	}

	[Fact]
	public async Task Pausing_a_leaf_finishes_every_worker_s_session_not_just_the_actor_s()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.multipause", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);
		var first = await port.StartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});
		var second = await port.StartSessionAsync(new() {
			Context = ContextFor(jobManagerId),
			LeafWorkId = leafId,
			WorkedByUserId = otherWorkerId,
		});

		var result = await port.PauseLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			ExpectedActiveSessions = [
				new() {
					Id = first.Id, Version = first.Version,
				},
				new() {
					Id = second.Id, Version = second.Version,
				},
			],
		});

		result.FinishedSessions.Should().HaveCount(2);
		result.FinishedSessions.Should().OnlyContain(s => s.FinishedAt != null);
		var stored = await ReadLeafSessionStateAsync(leafId);
		stored.Should().HaveCount(2).And.OnlyContain(row => !row.IsActive, "a pause leaves nobody clocked on");
	}

	[Fact]
	public async Task Pausing_a_leaf_leaves_its_achievement_untouched()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});

		_ = await port.PauseLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			ExpectedActiveSessions = [
				new() {
					Id = session.Id, Version = session.Version,
				},
			],
		});

		var achievementId = await ReadLeafAchievementAsync(leafId);
		achievementId.Should().Be(
			(short)Achievement.InProgress, "pausing stops the clocks, it does not close the job");
	}

	[Fact]
	public async Task Pausing_a_leaf_with_a_stale_expected_active_session_set_throws_a_concurrency_conflict()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var otherWorkerId = await SeedEmployeeAsync("Other Worker", "other.worker.stalepause", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);
		var first = await port.StartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});
		// The second worker clocked on after the caller read the page, so the confirmed set is short one
		// session -- silently pausing only the session the caller knew about is exactly the bug here.
		_ = await port.StartSessionAsync(new() {
			Context = ContextFor(jobManagerId),
			LeafWorkId = leafId,
			WorkedByUserId = otherWorkerId,
		});

		var act = () => port.PauseLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			ExpectedActiveSessions = [
				new() {
					Id = first.Id, Version = first.Version,
				},
			],
		});

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	[Fact]
	public async Task A_worker_with_no_node_control_cannot_pause_a_leaf_another_worker_is_also_clocked_onto()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var bystanderId = await SeedEmployeeAsync("Bystander", "bystander.pause", EmployeeRole.Worker);
		var port = CreateSessionPort(database.ConnectionString);
		var owned = await port.StartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});
		var bystanderSession = await port.StartSessionAsync(
			new() {
				Context = ContextFor(jobManagerId),
				LeafWorkId = leafId,
				WorkedByUserId = bystanderId,
			});

		var act = () => port.PauseLeafAsync(new() {
			Context = ContextFor(bystanderId),
			JobNodeId = leafId,
			ExpectedActiveSessions = [
				new() {
					Id = owned.Id, Version = owned.Version,
				},
				new() {
					Id = bystanderSession.Id, Version = bystanderSession.Version,
				},
			],
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Pausing_a_leaf_with_a_write_up_change_applies_it_in_the_same_commit()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});

		var result = await port.PauseLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			ExpectedActiveSessions = [
				new() {
					Id = session.Id, Version = session.Version,
				},
			],
			WriteUpChange = new() {
				NodeVersion = 1,
				WriteUp = "Stopped for the day",
			},
		});

		result.WriteUpChanged.Should().BeTrue();
		result.Node!.WriteUp.Should().Be("Stopped for the day");
	}

	[Fact]
	public async Task Pausing_a_leaf_with_a_stale_node_version_in_its_write_up_change_rolls_back_the_session_finishes()
	{
		var (_, jobManagerId, workerId, leafId) = await SeedReadyLeafAsync();
		var port = CreateSessionPort(database.ConnectionString);
		var session = await port.StartWorkAsync(new() {
			Context = ContextFor(workerId),
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});

		var act = () => port.PauseLeafAsync(new() {
			Context = ContextFor(jobManagerId),
			JobNodeId = leafId,
			ExpectedActiveSessions = [
				new() {
					Id = session.Id, Version = session.Version,
				},
			],
			WriteUpChange = new() {
				NodeVersion = 99,
				WriteUp = "Stopped for the day",
			},
		});

		await act.Should().ThrowAsync<ConcurrencyConflictException>();

		var stored = await ReadLeafSessionStateAsync(leafId);
		stored.Should().ContainSingle().Which.IsActive.Should()
			  .BeTrue("the rejected write-up change must roll the whole pause back");
	}
}
