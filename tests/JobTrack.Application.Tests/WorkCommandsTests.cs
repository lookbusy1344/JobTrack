namespace JobTrack.Application.Tests;

using Abstractions;
using AwesomeAssertions;
using NodaTime;

public sealed class WorkCommandsTests
{
	private static readonly AppUserId JobManagerId = new(1);
	private static readonly AppUserId WorkerId = new(2);
	private static readonly AppUserId OtherWorkerId = new(3);
	private static readonly JobNodeId RootId = new(1);

	private static (FakeJobNodeCommandPort NodePort, FakeWorkSessionCommandPort SessionPort) CreateSeededPorts()
	{
		var nodePort = new FakeJobNodeCommandPort();
		nodePort.SeedRoles(JobManagerId, EmployeeRole.JobManager);
		nodePort.SeedRoles(WorkerId, EmployeeRole.Worker);
		nodePort.SeedRoles(OtherWorkerId, EmployeeRole.Worker);
		nodePort.SeedNode(new() {
			Id = RootId,
			ParentId = null,
			Kind = NodeKind.Root,
			Description = "Root",
			PostedByUserId = JobManagerId,
			OwnerUserId = JobManagerId,
			Priority = Priority.Medium,
			PostedAt = nodePort.NowToReturn,
			HasChildren = false,
			HasLeafWork = false,
			Version = 1,
		});

		return (nodePort, new(nodePort));
	}

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	private static async Task<JobNodeId> CreateReadyLeafAsync(FakeJobNodeCommandPort nodePort)
	{
		var jobCommands = new JobCommands(nodePort);
		var leaf = await jobCommands.AddChildAsync(new() {
			Context = ContextFor(JobManagerId),
			ParentId = RootId,
			Description = "Do the thing",
			OwnerUserId = WorkerId,
			Priority = Priority.Medium,
		});
		await jobCommands.AttachLeafWorkAsync(
			new() { Context = ContextFor(JobManagerId), JobNodeId = leaf.Id });

		return leaf.Id;
	}

	[Fact]
	public async Task A_worker_can_start_a_session_for_their_own_work()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));

		var result = await sut.StartSessionAsync(new() { Context = ContextFor(WorkerId), LeafWorkId = leafId, WorkedByUserId = WorkerId });

		result.LeafWorkId.Should().Be(leafId);
		result.WorkedByUserId.Should().Be(WorkerId);
		result.FinishedAt.Should().BeNull();
	}

	[Fact]
	public async Task A_worker_cannot_start_a_session_for_another_worker()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));

		var act = () => sut.StartSessionAsync(new() { Context = ContextFor(OtherWorkerId), LeafWorkId = leafId, WorkedByUserId = WorkerId });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Starting_a_session_on_a_leaf_with_no_leaf_work_throws_not_found()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var jobCommands = new JobCommands(nodePort);
		var leaf = await jobCommands.AddChildAsync(new() {
			Context = ContextFor(JobManagerId),
			ParentId = RootId,
			Description = "Bare leaf",
			OwnerUserId = WorkerId,
			Priority = Priority.Medium,
		});
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));

		var act = () => sut.StartSessionAsync(new() { Context = ContextFor(WorkerId), LeafWorkId = leaf.Id, WorkedByUserId = WorkerId });

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Starting_a_second_active_session_for_the_same_worker_and_leaf_throws_an_invariant_violation()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));
		await sut.StartSessionAsync(new() { Context = ContextFor(WorkerId), LeafWorkId = leafId, WorkedByUserId = WorkerId });

		var act = () => sut.StartSessionAsync(new() { Context = ContextFor(WorkerId), LeafWorkId = leafId, WorkedByUserId = WorkerId });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-already-active");
	}

	[Fact]
	public async Task Starting_a_session_blocked_by_an_unsatisfied_prerequisite_throws_prerequisite_blocked()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var jobCommands = new JobCommands(nodePort);
		var requiredLeaf = await CreateReadyLeafAsync(nodePort);
		var dependentLeaf = await CreateReadyLeafAsync(nodePort);
		await jobCommands.AddPrerequisiteAsync(new() {
			Context = ContextFor(JobManagerId),
			RequiredJobId = requiredLeaf,
			DependentJobId = dependentLeaf,
		});
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));

		var act = () => sut.StartSessionAsync(new() { Context = ContextFor(WorkerId), LeafWorkId = dependentLeaf, WorkedByUserId = WorkerId });

		await act.Should().ThrowAsync<PrerequisiteBlockedException>();
	}

	[Fact]
	public async Task A_worker_can_start_a_session_with_a_backdated_start_instant()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));
		var backdatedStart = sessionPort.NowToReturn.Minus(Duration.FromHours(2));

		var result = await sut.StartSessionAsync(new() {
			Context = ContextFor(WorkerId),
			LeafWorkId = leafId,
			WorkedByUserId = WorkerId,
			StartedAt = backdatedStart,
		});

		result.StartedAt.Should().Be(backdatedStart);
	}

	[Fact]
	public async Task Starting_a_session_with_a_future_start_instant_throws_an_invariant_violation()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));

		var act = () => sut.StartSessionAsync(new() {
			Context = ContextFor(WorkerId),
			LeafWorkId = leafId,
			WorkedByUserId = WorkerId,
			StartedAt = sessionPort.NowToReturn.Plus(Duration.FromHours(2)),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-start-in-future");
	}

	[Fact]
	public async Task Starting_a_session_with_a_backdated_start_that_overlaps_another_session_throws_an_invariant_violation()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));
		var firstStart = sessionPort.NowToReturn.Minus(Duration.FromHours(3));
		var firstFinish = sessionPort.NowToReturn.Minus(Duration.FromHours(2));
		var first = await sut.StartSessionAsync(new() {
			Context = ContextFor(WorkerId),
			LeafWorkId = leafId,
			WorkedByUserId = WorkerId,
			StartedAt = firstStart,
		});
		await sut.FinishSessionAsync(new() {
			Context = ContextFor(WorkerId),
			SessionId = first.Id,
			Version = first.Version,
			FinishedAt = firstFinish,
		});

		var act = () => sut.StartSessionAsync(new() {
			Context = ContextFor(WorkerId),
			LeafWorkId = leafId,
			WorkedByUserId = WorkerId,
			StartedAt = firstStart.Plus(Duration.FromMinutes(30)),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-overlap");
	}

	[Fact]
	public async Task Starting_work_on_a_fresh_leaf_attaches_leaf_work_and_advances_it_to_in_progress()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var jobCommands = new JobCommands(nodePort);
		var leaf = await jobCommands.AddChildAsync(new() {
			Context = ContextFor(JobManagerId),
			ParentId = RootId,
			Description = "Bare leaf",
			OwnerUserId = WorkerId,
			Priority = Priority.Medium,
		});
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));

		var result = await sut.StartWorkAsync(new() { Context = ContextFor(WorkerId), JobNodeId = leaf.Id, WorkedByUserId = WorkerId });

		result.LeafWorkId.Should().Be(leaf.Id);
		result.FinishedAt.Should().BeNull();
		nodePort.FindLeafWork(leaf.Id)!.Achievement.Should().Be(Achievement.InProgress);
	}

	[Fact]
	public async Task Starting_work_on_an_already_attached_waiting_leaf_advances_it_to_in_progress()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));

		_ = await sut.StartWorkAsync(new() { Context = ContextFor(WorkerId), JobNodeId = leafId, WorkedByUserId = WorkerId });

		nodePort.FindLeafWork(leafId)!.Achievement.Should().Be(Achievement.InProgress);
	}

	[Fact]
	public async Task Starting_work_when_already_in_progress_from_another_workers_session_does_not_touch_achievement_again()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));
		await sut.StartWorkAsync(new() { Context = ContextFor(WorkerId), JobNodeId = leafId, WorkedByUserId = WorkerId });
		var achievementAfterFirstStart = nodePort.FindLeafWork(leafId)!;

		var result = await sut.StartWorkAsync(new() { Context = ContextFor(JobManagerId), JobNodeId = leafId, WorkedByUserId = OtherWorkerId });

		result.WorkedByUserId.Should().Be(OtherWorkerId);
		nodePort.FindLeafWork(leafId)!.Version.Should().Be(achievementAfterFirstStart.Version);
	}

	[Fact]
	public async Task Starting_work_blocked_by_an_unsatisfied_prerequisite_throws_prerequisite_blocked()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var jobCommands = new JobCommands(nodePort);
		var requiredLeaf = await CreateReadyLeafAsync(nodePort);
		var dependentLeaf = await jobCommands.AddChildAsync(new() {
			Context = ContextFor(JobManagerId),
			ParentId = RootId,
			Description = "Blocked bare leaf",
			OwnerUserId = WorkerId,
			Priority = Priority.Medium,
		});
		await jobCommands.AddPrerequisiteAsync(new() {
			Context = ContextFor(JobManagerId),
			RequiredJobId = requiredLeaf,
			DependentJobId = dependentLeaf.Id,
		});
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));

		var act = () => sut.StartWorkAsync(new() { Context = ContextFor(WorkerId), JobNodeId = dependentLeaf.Id, WorkedByUserId = WorkerId });

		await act.Should().ThrowAsync<PrerequisiteBlockedException>();
	}

	[Fact]
	public async Task Starting_work_a_second_time_for_the_same_worker_and_leaf_throws_an_invariant_violation()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));
		await sut.StartWorkAsync(new() { Context = ContextFor(WorkerId), JobNodeId = leafId, WorkedByUserId = WorkerId });

		var act = () => sut.StartWorkAsync(new() { Context = ContextFor(WorkerId), JobNodeId = leafId, WorkedByUserId = WorkerId });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-already-active");
	}

	[Fact]
	public async Task A_worker_cannot_start_work_for_another_worker()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));

		var act = () => sut.StartWorkAsync(new() { Context = ContextFor(OtherWorkerId), JobNodeId = leafId, WorkedByUserId = WorkerId });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Starting_work_on_the_root_throws_an_invariant_violation()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));

		var act = () => sut.StartWorkAsync(new() { Context = ContextFor(JobManagerId), JobNodeId = RootId, WorkedByUserId = JobManagerId });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-is-root-cannot-attach-leaf-work");
	}

	[Fact]
	public async Task StartWorkAsync_rejects_a_null_request()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));

		var act = () => sut.StartWorkAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task A_worker_can_finish_a_session_with_a_backdated_finish_instant()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));
		var backdatedStart = sessionPort.NowToReturn.Minus(Duration.FromHours(2));
		var session = await sut.StartSessionAsync(new() {
			Context = ContextFor(WorkerId),
			LeafWorkId = leafId,
			WorkedByUserId = WorkerId,
			StartedAt = backdatedStart,
		});
		var backdatedFinish = backdatedStart.Plus(Duration.FromHours(1));

		var result = await sut.FinishSessionAsync(new() {
			Context = ContextFor(WorkerId),
			SessionId = session.Id,
			Version = session.Version,
			FinishedAt = backdatedFinish,
		});

		result.FinishedAt.Should().Be(backdatedFinish);
	}

	[Fact]
	public async Task Finishing_a_session_with_a_finish_instant_before_its_start_throws_an_invariant_violation()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));
		var session = await sut.StartSessionAsync(new() { Context = ContextFor(WorkerId), LeafWorkId = leafId, WorkedByUserId = WorkerId });

		var act = () => sut.FinishSessionAsync(new() {
			Context = ContextFor(WorkerId),
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
		var (nodePort, sessionPort) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));
		var session = await sut.StartSessionAsync(new() { Context = ContextFor(WorkerId), LeafWorkId = leafId, WorkedByUserId = WorkerId });

		var act = () => sut.FinishSessionAsync(new() {
			Context = ContextFor(WorkerId),
			SessionId = session.Id,
			Version = session.Version,
			FinishedAt = sessionPort.NowToReturn.Plus(Duration.FromHours(2)),
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("work-session-finish-in-future");
	}

	[Fact]
	public async Task Finishing_a_session_sets_finished_at_and_bumps_the_version()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));
		var session = await sut.StartSessionAsync(new() { Context = ContextFor(WorkerId), LeafWorkId = leafId, WorkedByUserId = WorkerId });

		var result = await sut.FinishSessionAsync(
			new() { Context = ContextFor(WorkerId), SessionId = session.Id, Version = session.Version });

		result.FinishedAt.Should().Be(sessionPort.NowToReturn);
		result.Version.Should().Be(2);
	}

	[Fact]
	public async Task A_worker_cannot_finish_another_workers_session()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));
		var session = await sut.StartSessionAsync(new() { Context = ContextFor(WorkerId), LeafWorkId = leafId, WorkedByUserId = WorkerId });

		var act = () => sut.FinishSessionAsync(new() { Context = ContextFor(OtherWorkerId), SessionId = session.Id, Version = session.Version });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Finishing_with_a_stale_version_throws_a_concurrency_conflict()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));
		var session = await sut.StartSessionAsync(new() { Context = ContextFor(WorkerId), LeafWorkId = leafId, WorkedByUserId = WorkerId });

		var act = () => sut.FinishSessionAsync(new() { Context = ContextFor(WorkerId), SessionId = session.Id, Version = session.Version + 1 });

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	[Fact]
	public async Task Correcting_a_session_replaces_its_interval()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));
		var session = await sut.StartSessionAsync(new() { Context = ContextFor(WorkerId), LeafWorkId = leafId, WorkedByUserId = WorkerId });
		var correctedStart = session.StartedAt.Minus(Duration.FromHours(1));
		var correctedFinish = session.StartedAt;

		var result = await sut.CorrectSessionAsync(new() {
			Context = ContextFor(WorkerId),
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
		var (nodePort, sessionPort) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));
		var session = await sut.StartSessionAsync(new() { Context = ContextFor(WorkerId), LeafWorkId = leafId, WorkedByUserId = WorkerId });

		var act = () => sut.CorrectSessionAsync(new() {
			Context = ContextFor(WorkerId),
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
		var (nodePort, sessionPort) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));
		var firstStart = Instant.FromUtc(2026, 1, 1, 9, 0);
		var firstFinish = Instant.FromUtc(2026, 1, 1, 10, 0);
		var first = await sut.StartSessionAsync(new() { Context = ContextFor(WorkerId), LeafWorkId = leafId, WorkedByUserId = WorkerId });
		await sut.FinishSessionAsync(
			new() { Context = ContextFor(WorkerId), SessionId = first.Id, Version = first.Version });
		first = await sut.CorrectSessionAsync(new() {
			Context = ContextFor(WorkerId),
			SessionId = first.Id,
			StartedAt = firstStart,
			FinishedAt = firstFinish,
			Reason = "Establish a fixed historical interval",
			Version = first.Version + 1,
		});
		var second = await sut.StartSessionAsync(new() { Context = ContextFor(WorkerId), LeafWorkId = leafId, WorkedByUserId = WorkerId });

		var act = () => sut.CorrectSessionAsync(new() {
			Context = ContextFor(WorkerId),
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
	public async Task A_job_manager_can_transition_a_leaf_from_waiting_to_in_progress()
	{
		var (nodePort, _) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(new FakeWorkSessionCommandPort(nodePort), new FakeAchievementCommandPort(nodePort));

		var result = await sut.SetAchievementAsync(new() {
			Context = ContextFor(JobManagerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.InProgress,
			Reason = "Work has started",
			Version = 1,
		});

		result.Achievement.Should().Be(Achievement.InProgress);
		result.Version.Should().Be(2);
	}

	[Fact]
	public async Task Setting_achievement_rejects_none_synchronously()
	{
		var (nodePort, _) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(new FakeWorkSessionCommandPort(nodePort), new FakeAchievementCommandPort(nodePort));
		var request = new SetAchievementRequest {
			Context = ContextFor(JobManagerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.None,
			Reason = "Invalid default value",
			Version = 1,
		};

		Action act = () => _ = sut.SetAchievementAsync(request);

		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public async Task An_impermissible_transition_throws_an_invariant_violation()
	{
		var (nodePort, _) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(new FakeWorkSessionCommandPort(nodePort), new FakeAchievementCommandPort(nodePort));

		var act = () => sut.SetAchievementAsync(new() {
			Context = ContextFor(JobManagerId),
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
		var (nodePort, _) = CreateSeededPorts();
		var jobCommands = new JobCommands(nodePort);
		var requiredLeaf = await CreateReadyLeafAsync(nodePort);
		var dependentLeaf = await CreateReadyLeafAsync(nodePort);
		await jobCommands.AddPrerequisiteAsync(new() {
			Context = ContextFor(JobManagerId),
			RequiredJobId = requiredLeaf,
			DependentJobId = dependentLeaf,
		});
		var sut = new WorkCommands(new FakeWorkSessionCommandPort(nodePort), new FakeAchievementCommandPort(nodePort));
		await sut.SetAchievementAsync(new() {
			Context = ContextFor(JobManagerId),
			JobNodeId = dependentLeaf,
			NewAchievement = Achievement.InProgress,
			Reason = "Attempting despite the prerequisite",
			Version = 1,
		});

		var act = () => sut.SetAchievementAsync(new() {
			Context = ContextFor(JobManagerId),
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
		var (nodePort, _) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(new FakeWorkSessionCommandPort(nodePort), new FakeAchievementCommandPort(nodePort));
		await sut.SetAchievementAsync(new() {
			Context = ContextFor(JobManagerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.InProgress,
			Reason = "Starting",
			Version = 1,
		});
		await sut.SetAchievementAsync(new() {
			Context = ContextFor(JobManagerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.Unsuccessful,
			Reason = "Did not pan out",
			Version = 2,
		});

		var act = () => sut.SetAchievementAsync(new() {
			Context = ContextFor(WorkerId),
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
		var (nodePort, _) = CreateSeededPorts();
		var leafId = await CreateReadyLeafAsync(nodePort);
		var sut = new WorkCommands(new FakeWorkSessionCommandPort(nodePort), new FakeAchievementCommandPort(nodePort));
		await sut.SetAchievementAsync(new() {
			Context = ContextFor(JobManagerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.InProgress,
			Reason = "Starting",
			Version = 1,
		});
		await sut.SetAchievementAsync(new() {
			Context = ContextFor(JobManagerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.Unsuccessful,
			Reason = "Did not pan out",
			Version = 2,
		});

		var result = await sut.SetAchievementAsync(new() {
			Context = ContextFor(JobManagerId),
			JobNodeId = leafId,
			NewAchievement = Achievement.Waiting,
			Reason = "Trying again",
			Version = 3,
		});

		result.Achievement.Should().Be(Achievement.Waiting);
	}

	[Fact]
	public void Constructor_rejects_a_null_session_port()
	{
		var (nodePort, _) = CreateSeededPorts();

		var act = () => new WorkCommands(null!, new FakeAchievementCommandPort(nodePort));

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public void Constructor_rejects_a_null_achievement_port()
	{
		var (_, sessionPort) = CreateSeededPorts();

		var act = () => new WorkCommands(sessionPort, null!);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public async Task StartSessionAsync_rejects_a_null_request()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));

		var act = () => sut.StartSessionAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task FinishSessionAsync_rejects_a_null_request()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));

		var act = () => sut.FinishSessionAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task CorrectSessionAsync_rejects_a_null_request()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));

		var act = () => sut.CorrectSessionAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task SetAchievementAsync_rejects_a_null_request()
	{
		var (nodePort, sessionPort) = CreateSeededPorts();
		var sut = new WorkCommands(sessionPort, new FakeAchievementCommandPort(nodePort));

		var act = () => sut.SetAchievementAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}
}
