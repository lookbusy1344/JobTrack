namespace JobTrack.Application.Tests;

using System.Diagnostics;
using Abstractions;
using AwesomeAssertions;
using NodaTime;

public sealed class JobCommandsTests
{
	/// <summary>
	///     A fixed "import happened here" instant. The planner's work validation is pure — it compares
	///     the batch's own instants against each other, never against the wall clock — so these need no
	///     relationship to the real current time.
	/// </summary>
	private static readonly Instant ImportReference = Instant.FromUtc(2026, 7, 18, 9, 0, 0);

	private static readonly Instant OneDayAgo = ImportReference - Duration.FromDays(1);
	private static readonly Instant TwoDaysAgo = ImportReference - Duration.FromDays(2);
	private static readonly Instant ThreeDaysAgo = ImportReference - Duration.FromDays(3);

	private static readonly AppUserId JobManagerId = new(1);
	private static readonly AppUserId OwnerWorkerId = new(2);
	private static readonly AppUserId OtherWorkerId = new(3);
	private static readonly AppUserId AdministratorId = new(4);
	private static readonly JobNodeId RootId = new(1);

	private static FakeJobNodeCommandPort CreateSeededPort()
	{
		var port = new FakeJobNodeCommandPort();
		port.SeedRoles(JobManagerId, EmployeeRole.JobManager);
		port.SeedRoles(OwnerWorkerId, EmployeeRole.Worker);
		port.SeedRoles(OtherWorkerId, EmployeeRole.Worker);
		port.SeedRoles(AdministratorId, EmployeeRole.Administrator);

		port.SeedNode(new() {
			Id = RootId,
			ParentId = null,
			Kind = NodeKind.Root,
			Description = "Root",
			PostedByUserId = JobManagerId,
			OwnerUserId = JobManagerId,
			Priority = Priority.Medium,
			PostedAt = port.NowToReturn,
			HasChildren = false,
			HasLeafWork = false,
			Version = 1,
		});

		return port;
	}

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	private static CreateJobNodeRequest CreateBranchRequest(AppUserId actor, AppUserId owner, JobNodeId parentId) => new() {
		Context = ContextFor(actor),
		ParentId = parentId,
		Description = "Do the thing",
		OwnerUserId = owner,
		Priority = Priority.Medium,
	};

	private static ImportSubtreeRequest ImportRequestWith(EquatableArray<ImportSubtreeNodeSpec> nodes) => new() {
		Context = ContextFor(JobManagerId),
		ParentId = RootId,
		Nodes = nodes,
	};

	/// <summary>A worked leaf's spec, defaulting to the successfully-closed case.</summary>
	private static ImportSubtreeLeafWorkSpec WorkFrom(Instant startedAt, Instant? finishedAt) => new() {
		WorkedByUserId = OwnerWorkerId,
		StartedAt = startedAt,
		FinishedAt = finishedAt,
		Achievement = Achievement.Success,
	};

	private static ImportSubtreeNodeSpec PlainNode(long localId, long? parentLocalId = null) => new() {
		LocalId = localId,
		ParentLocalId = parentLocalId,
		Description = $"Node {localId}",
		OwnerUserId = OwnerWorkerId,
		Priority = Priority.Medium,
	};

	private static ImportSubtreeNodeSpec WorkedNode(
		long localId, long? parentLocalId, ImportSubtreeLeafWorkSpec leafWork, EquatableArray<long> prerequisiteLocalIds = default) =>
		PlainNode(localId, parentLocalId) with { LeafWork = leafWork, PrerequisiteLocalIds = prerequisiteLocalIds };

	[Fact]
	public async Task A_job_manager_can_create_a_branch_under_the_root()
	{
		var sut = new JobCommands(CreateSeededPort());

		var result = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));

		result.Kind.Should().Be(NodeKind.Leaf);
		result.ParentId.Should().Be(RootId);
		result.OwnerUserId.Should().Be(OwnerWorkerId);
		result.Version.Should().Be(1);
	}

	[Fact]
	public void Creating_a_node_rejects_an_unspecified_priority_synchronously()
	{
		var sut = new JobCommands(CreateSeededPort());
		var request = CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId) with { Priority = Priority.Unspecified };

		Action act = () => _ = sut.AddChildAsync(request);

		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public async Task A_command_emits_one_bounded_activity_without_request_payload()
	{
		var stopped = new List<Activity>();
		using var listener = new ActivityListener {
			ShouldListenTo = source => source.Name == JobTrackDiagnostics.ActivitySourceName,
			Sample = static (ref _) => ActivitySamplingResult.AllData,
			ActivityStopped = stopped.Add,
		};
		ActivitySource.AddActivityListener(listener);
		var sut = new JobCommands(CreateSeededPort());

		_ = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));

		var operation = stopped.Should().ContainSingle(activity => activity.OperationName == "jobs.add-child").Which;
		operation.Status.Should().Be(ActivityStatusCode.Ok);
		operation.GetTagItem("jobtrack.actor_id").Should().Be(JobManagerId.Value);
		operation.GetTagItem("jobtrack.correlation_id").Should().NotBeNull();
		operation.GetTagItem("jobtrack.target.node_id").Should().Be(RootId.Value);
		operation.GetTagItem("jobtrack.description").Should().BeNull();
	}

	[Fact]
	public async Task A_pre_cancelled_command_propagates_cancellation_without_retrying()
	{
		var sut = new JobCommands(CreateSeededPort());
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();

		var act = () => sut.AddChildAsync(
			CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId), cancellation.Token);

		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task A_job_manager_can_create_a_leaf_under_the_root()
	{
		var sut = new JobCommands(CreateSeededPort());

		var result = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));

		result.Kind.Should().Be(NodeKind.Leaf);
	}

	[Fact]
	public async Task A_worker_cannot_create_a_node_under_a_root_they_do_not_own()
	{
		var sut = new JobCommands(CreateSeededPort());

		var act = () => sut.AddChildAsync(CreateBranchRequest(OtherWorkerId, OtherWorkerId, RootId));

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task A_worker_can_create_a_node_under_a_branch_they_own()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var ownedBranch = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));

		var result = await sut.AddChildAsync(CreateBranchRequest(OwnerWorkerId, OwnerWorkerId, ownedBranch.Id));

		result.ParentId.Should().Be(ownedBranch.Id);
	}

	[Fact]
	public async Task Creating_a_node_under_a_nonexistent_parent_throws_not_found()
	{
		var sut = new JobCommands(CreateSeededPort());

		var act = () => sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, new(999)));

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Editing_a_node_replaces_its_editable_fields_and_bumps_the_version()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var branch = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));

		var result = await sut.EditAsync(new() {
			Context = ContextFor(JobManagerId),
			NodeId = branch.Id,
			Description = "Updated description",
			OwnerUserId = OwnerWorkerId,
			Priority = Priority.High,
			Version = branch.Version,
		});

		result.Description.Should().Be("Updated description");
		result.Priority.Should().Be(Priority.High);
		result.Version.Should().Be(2);
	}

	[Fact]
	public async Task Editing_a_node_rejects_an_unspecified_priority_synchronously()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var branch = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		var request = new EditJobNodeRequest {
			Context = ContextFor(JobManagerId),
			NodeId = branch.Id,
			Description = "Updated description",
			OwnerUserId = OwnerWorkerId,
			Priority = Priority.Unspecified,
			Version = branch.Version,
		};

		Action act = () => _ = sut.EditAsync(request);

		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public async Task Editing_with_a_stale_version_throws_a_concurrency_conflict()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var branch = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));

		var act = () => sut.EditAsync(new() {
			Context = ContextFor(JobManagerId),
			NodeId = branch.Id,
			Description = "Updated description",
			OwnerUserId = OwnerWorkerId,
			Priority = Priority.High,
			Version = branch.Version + 1,
		});

		await act.Should().ThrowAsync<ConcurrencyConflictException>();
	}

	[Fact]
	public async Task Moving_a_node_updates_its_parent()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var branchA = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, JobManagerId, RootId));
		var branchB = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, JobManagerId, RootId));

		var result = await sut.MoveAsync(new() {
			Context = ContextFor(JobManagerId),
			NodeId = branchB.Id,
			NewParentId = branchA.Id,
			Version = branchB.Version,
		});

		result.ParentId.Should().Be(branchA.Id);
	}

	[Fact]
	public async Task Moving_a_node_under_its_own_descendant_throws_an_invariant_violation()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var parent = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, JobManagerId, RootId));
		var child = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, JobManagerId, parent.Id));

		var act = () => sut.MoveAsync(new() {
			Context = ContextFor(JobManagerId),
			NodeId = parent.Id,
			NewParentId = child.Id,
			Version = parent.Version,
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-move-would-cycle");
	}

	[Fact]
	public async Task A_worker_cannot_move_a_node_into_a_subtree_they_do_not_own()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var ownedBranch = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		var otherBranch = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OtherWorkerId, RootId));

		var act = () => sut.MoveAsync(new() {
			Context = ContextFor(OwnerWorkerId),
			NodeId = ownedBranch.Id,
			NewParentId = otherBranch.Id,
			Version = ownedBranch.Version,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Archiving_a_node_sets_archived_at_without_deleting_it()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var branch = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));

		var result = await sut.ArchiveAsync(new() { Context = ContextFor(JobManagerId), NodeId = branch.Id, Version = branch.Version });

		result.ArchivedAt.Should().Be(port.NowToReturn);
	}

	[Fact]
	public async Task Deleting_an_unused_node_removes_it()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var branch = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));

		await sut.DeleteAsync(new() { Context = ContextFor(JobManagerId), NodeId = branch.Id, Version = branch.Version });

		var act = () => sut.EditAsync(new() {
			Context = ContextFor(JobManagerId),
			NodeId = branch.Id,
			Description = "irrelevant",
			OwnerUserId = OwnerWorkerId,
			Priority = Priority.Medium,
			Version = 1,
		});
		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task Deleting_a_node_with_dependent_data_throws_an_invariant_violation()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var branch = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		port.MarkUndeletable(branch.Id);

		var act = () =>
			sut.DeleteAsync(new() { Context = ContextFor(JobManagerId), NodeId = branch.Id, Version = branch.Version });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-not-deletable");
	}

	[Fact]
	public async Task Deleting_the_root_throws_an_invariant_violation()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);

		var act = () => sut.DeleteAsync(new() { Context = ContextFor(JobManagerId), NodeId = RootId, Version = 1 });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-is-root-cannot-delete");
	}

	[Fact]
	public async Task Deleting_a_node_with_children_throws_an_invariant_violation()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var parent = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		_ = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, parent.Id));

		var act = () =>
			sut.DeleteAsync(new() { Context = ContextFor(JobManagerId), NodeId = parent.Id, Version = parent.Version });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-has-children-cannot-delete");
	}

	[Fact]
	public async Task Deleting_a_node_with_a_prerequisite_edge_throws_an_invariant_violation()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var required = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		var dependent = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		await sut.AddPrerequisiteAsync(new() { Context = ContextFor(JobManagerId), RequiredJobId = required.Id, DependentJobId = dependent.Id });

		var act = () => sut.DeleteAsync(new() { Context = ContextFor(JobManagerId), NodeId = required.Id, Version = required.Version });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-has-prerequisites-cannot-delete");
	}

	[Fact]
	public async Task Deleting_a_leaf_with_unused_leaf_work_removes_it()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var leaf = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		_ = await sut.AttachLeafWorkAsync(new() { Context = ContextFor(JobManagerId), JobNodeId = leaf.Id });

		await sut.DeleteAsync(new() { Context = ContextFor(JobManagerId), NodeId = leaf.Id, Version = leaf.Version });

		var act = () => sut.EditAsync(new() {
			Context = ContextFor(JobManagerId),
			NodeId = leaf.Id,
			Description = "irrelevant",
			OwnerUserId = OwnerWorkerId,
			Priority = Priority.Medium,
			Version = 1,
		});
		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task A_non_administrator_cannot_delete_a_worked_leaf()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var leaf = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		_ = await sut.AttachLeafWorkAsync(new() { Context = ContextFor(JobManagerId), JobNodeId = leaf.Id });
		port.MarkLeafWorked(leaf.Id);

		var act = () => sut.DeleteAsync(new() {
			Context = ContextFor(JobManagerId),
			NodeId = leaf.Id,
			Version = leaf.Version,
			Reason = "Trying anyway.",
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task An_administrator_deleting_a_worked_leaf_without_a_reason_throws_an_invariant_violation()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var leaf = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		_ = await sut.AttachLeafWorkAsync(new() { Context = ContextFor(JobManagerId), JobNodeId = leaf.Id });
		port.MarkLeafWorked(leaf.Id);

		var act = () => sut.DeleteAsync(new() { Context = ContextFor(AdministratorId), NodeId = leaf.Id, Version = leaf.Version });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-delete-worked-leaf-reason-required");
	}

	[Fact]
	public async Task An_administrator_can_delete_a_worked_leaf_with_a_reason()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var leaf = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		_ = await sut.AttachLeafWorkAsync(new() { Context = ContextFor(JobManagerId), JobNodeId = leaf.Id });
		port.MarkLeafWorked(leaf.Id);

		await sut.DeleteAsync(new() {
			Context = ContextFor(AdministratorId),
			NodeId = leaf.Id,
			Version = leaf.Version,
			Reason = "Created and worked in error; duplicate of another job.",
		});

		var act = () => sut.EditAsync(new() {
			Context = ContextFor(JobManagerId),
			NodeId = leaf.Id,
			Description = "irrelevant",
			OwnerUserId = OwnerWorkerId,
			Priority = Priority.Medium,
			Version = 1,
		});
		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public void Constructor_rejects_a_null_port()
	{
		var act = () => new JobCommands(null!);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public async Task Attaching_leaf_work_to_a_bare_leaf_starts_at_waiting()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var leaf = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));

		var result = await sut.AttachLeafWorkAsync(new() {
			Context = ContextFor(JobManagerId),
			JobNodeId = leaf.Id,
			FullCriteria = "Done when shipped",
		});

		result.Achievement.Should().Be(Achievement.Waiting);
		result.FullCriteria.Should().Be("Done when shipped");
	}

	[Fact]
	public async Task Attaching_leaf_work_to_a_branch_throws_an_invariant_violation()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var branch = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		_ = await sut.AddChildAsync(new() {
			Context = ContextFor(JobManagerId),
			ParentId = branch.Id,
			Description = "Child under branch",
			OwnerUserId = OwnerWorkerId,
			Priority = Priority.Medium,
		});

		var act = () => sut.AttachLeafWorkAsync(new() { Context = ContextFor(JobManagerId), JobNodeId = branch.Id });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-has-children-cannot-attach-leaf-work");
	}

	[Fact]
	public async Task Attaching_leaf_work_to_the_root_throws_an_invariant_violation()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);

		var act = () => sut.AttachLeafWorkAsync(new() { Context = ContextFor(JobManagerId), JobNodeId = RootId });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-node-is-root-cannot-attach-leaf-work");
	}

	[Fact]
	public async Task Attaching_leaf_work_twice_throws_an_invariant_violation()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var leaf = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		await sut.AttachLeafWorkAsync(new() { Context = ContextFor(JobManagerId), JobNodeId = leaf.Id });

		var act = () => sut.AttachLeafWorkAsync(new() { Context = ContextFor(JobManagerId), JobNodeId = leaf.Id });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("leaf-work-already-attached");
	}

	[Fact]
	public async Task Decomposing_a_worked_leaf_creates_the_expected_children_and_converts_it_to_a_branch()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var leaf = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		await sut.AttachLeafWorkAsync(new() { Context = ContextFor(JobManagerId), JobNodeId = leaf.Id });

		var result = await sut.DecomposeWorkedLeafAsync(new() {
			Context = ContextFor(JobManagerId),
			LeafNodeId = leaf.Id,
			Version = leaf.Version,
			BranchDescription = "Umbrella job",
			ExistingWorkDescription = "The work already done",
			NewChildren = [
				new() { Description = "New sub-job", OwnerUserId = OwnerWorkerId, Priority = Priority.Medium },
			],
		});

		result.BranchId.Should().Be(leaf.Id);
		result.NewChildIds.Should().HaveCount(1);

		var branch = await sut.EditAsync(new() {
			Context = ContextFor(JobManagerId),
			NodeId = result.BranchId,
			Description = "Umbrella job",
			OwnerUserId = OwnerWorkerId,
			Priority = Priority.Medium,
			Version = result.BranchVersion,
		});
		branch.Kind.Should().Be(NodeKind.Branch);
	}

	[Fact]
	public async Task Decomposing_a_leaf_with_no_leaf_work_throws_an_invariant_violation()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var leaf = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));

		var act = () => sut.DecomposeWorkedLeafAsync(new() {
			Context = ContextFor(JobManagerId),
			LeafNodeId = leaf.Id,
			Version = leaf.Version,
			BranchDescription = "Umbrella job",
			ExistingWorkDescription = "The work already done",
			NewChildren = [],
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("leaf-work-not-attached");
	}

	[Fact]
	public async Task A_worker_cannot_attach_leaf_work_to_a_leaf_they_do_not_own()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var leaf = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OtherWorkerId, RootId));

		var act = () => sut.AttachLeafWorkAsync(new() { Context = ContextFor(OwnerWorkerId), JobNodeId = leaf.Id });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Adding_a_prerequisite_between_unrelated_leaves_succeeds()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var required = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		var dependent = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));

		await sut.AddPrerequisiteAsync(new() { Context = ContextFor(JobManagerId), RequiredJobId = required.Id, DependentJobId = dependent.Id });

		var act = () => sut.AddPrerequisiteAsync(new() {
			Context = ContextFor(JobManagerId),
			RequiredJobId = required.Id,
			DependentJobId = dependent.Id,
		});
		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-prerequisite-already-exists");
	}

	[Fact]
	public async Task A_job_cannot_require_itself()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var leaf = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));

		var act = () => sut.AddPrerequisiteAsync(new() { Context = ContextFor(JobManagerId), RequiredJobId = leaf.Id, DependentJobId = leaf.Id });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-prerequisite-not-self");
	}

	[Fact]
	public async Task A_prerequisite_edge_between_ancestor_and_descendant_is_rejected()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var parent = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		var child = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, parent.Id));

		var act = () => sut.AddPrerequisiteAsync(new() { Context = ContextFor(JobManagerId), RequiredJobId = parent.Id, DependentJobId = child.Id });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-prerequisite-is-hierarchy-edge");
	}

	[Fact]
	public async Task A_prerequisite_edge_that_would_create_a_cycle_is_rejected()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var a = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		var b = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		await sut.AddPrerequisiteAsync(
			new() { Context = ContextFor(JobManagerId), RequiredJobId = a.Id, DependentJobId = b.Id });

		var act = () => sut.AddPrerequisiteAsync(new() { Context = ContextFor(JobManagerId), RequiredJobId = b.Id, DependentJobId = a.Id });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-prerequisite-would-cycle");
	}

	[Fact]
	public async Task Removing_a_prerequisite_allows_it_to_be_added_again()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var required = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		var dependent = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		await sut.AddPrerequisiteAsync(new() { Context = ContextFor(JobManagerId), RequiredJobId = required.Id, DependentJobId = dependent.Id });

		await sut.RemovePrerequisiteAsync(new() { Context = ContextFor(JobManagerId), RequiredJobId = required.Id, DependentJobId = dependent.Id });

		await sut.AddPrerequisiteAsync(new() { Context = ContextFor(JobManagerId), RequiredJobId = required.Id, DependentJobId = dependent.Id });
	}

	[Fact]
	public async Task Removing_a_nonexistent_prerequisite_throws_not_found()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var required = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		var dependent = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));

		var act = () => sut.RemovePrerequisiteAsync(new() {
			Context = ContextFor(JobManagerId),
			RequiredJobId = required.Id,
			DependentJobId = dependent.Id,
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task A_worker_cannot_add_a_prerequisite_involving_a_job_they_do_not_own()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);
		var required = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OwnerWorkerId, RootId));
		var dependent = await sut.AddChildAsync(CreateBranchRequest(JobManagerId, OtherWorkerId, RootId));

		var act = () => sut.AddPrerequisiteAsync(new() {
			Context = ContextFor(OwnerWorkerId),
			RequiredJobId = required.Id,
			DependentJobId = dependent.Id,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Importing_a_subtree_creates_every_node_and_prerequisite_edge_in_one_call()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);

		var result = await sut.ImportSubtreeAsync(new() {
			Context = ContextFor(JobManagerId),
			ParentId = RootId,
			Nodes = [
				new() {
					LocalId = 1,
					ParentLocalId = null,
					Description = "Branch",
					OwnerUserId = OwnerWorkerId,
					Priority = Priority.Medium,
				},
				new() {
					LocalId = 2,
					ParentLocalId = 1,
					Description = "Child A",
					OwnerUserId = OwnerWorkerId,
					Priority = Priority.Medium,
				},
				new() {
					LocalId = 3,
					ParentLocalId = 1,
					Description = "Child B",
					OwnerUserId = OwnerWorkerId,
					Priority = Priority.Medium,
					PrerequisiteLocalIds = [2],
				},
			],
		});

		result.Nodes.Should().HaveCount(3);
		var branchId = result.Nodes.Single(n => n.LocalId == 1).JobNodeId;
		var childAId = result.Nodes.Single(n => n.LocalId == 2).JobNodeId;
		var childBId = result.Nodes.Single(n => n.LocalId == 3).JobNodeId;

		var branch = await sut.EditAsync(new() {
			Context = ContextFor(JobManagerId),
			NodeId = branchId,
			Description = "Branch",
			OwnerUserId = OwnerWorkerId,
			Priority = Priority.Medium,
			Version = 1,
		});
		branch.Kind.Should().Be(NodeKind.Branch);

		var act = () => sut.AddPrerequisiteAsync(new() { Context = ContextFor(JobManagerId), RequiredJobId = childAId, DependentJobId = childBId });
		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("job-prerequisite-already-exists");
	}

	[Fact]
	public async Task Importing_an_empty_batch_throws_an_invariant_violation()
	{
		var sut = new JobCommands(CreateSeededPort());

		var act = () => sut.ImportSubtreeAsync(new() { Context = ContextFor(JobManagerId), ParentId = RootId, Nodes = [] });

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("import-subtree-empty");
	}

	[Fact]
	public async Task Importing_a_batch_with_a_duplicate_local_id_throws_an_invariant_violation()
	{
		var sut = new JobCommands(CreateSeededPort());

		var act = () => sut.ImportSubtreeAsync(new() {
			Context = ContextFor(JobManagerId),
			ParentId = RootId,
			Nodes = [
				new() { LocalId = 1, Description = "First", OwnerUserId = OwnerWorkerId, Priority = Priority.Medium },
				new() { LocalId = 1, Description = "Second", OwnerUserId = OwnerWorkerId, Priority = Priority.Medium },
			],
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("import-subtree-duplicate-local-id");
	}

	[Fact]
	public async Task Importing_a_batch_with_a_parent_reference_cycle_throws_an_invariant_violation()
	{
		var sut = new JobCommands(CreateSeededPort());

		var act = () => sut.ImportSubtreeAsync(new() {
			Context = ContextFor(JobManagerId),
			ParentId = RootId,
			Nodes = [
				new() {
					LocalId = 1,
					ParentLocalId = 2,
					Description = "A",
					OwnerUserId = OwnerWorkerId,
					Priority = Priority.Medium,
				},
				new() {
					LocalId = 2,
					ParentLocalId = 1,
					Description = "B",
					OwnerUserId = OwnerWorkerId,
					Priority = Priority.Medium,
				},
			],
		});

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("import-subtree-parent-cycle");
	}

	[Fact]
	public async Task Importing_a_subtree_under_a_node_the_actor_may_not_manage_throws_authorization_denied()
	{
		var sut = new JobCommands(CreateSeededPort());

		var act = () => sut.ImportSubtreeAsync(new() {
			Context = ContextFor(OtherWorkerId),
			ParentId = RootId,
			Nodes = [
				new() { LocalId = 1, Description = "Branch", OwnerUserId = OtherWorkerId, Priority = Priority.Medium },
			],
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Importing_a_batch_that_records_work_on_a_node_with_children_throws_an_invariant_violation()
	{
		var sut = new JobCommands(CreateSeededPort());

		var act = () => sut.ImportSubtreeAsync(ImportRequestWith([
			WorkedNode(1, null, WorkFrom(TwoDaysAgo, OneDayAgo)),
			PlainNode(2, 1),
		]));

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("import-subtree-work-on-branch");
	}

	[Theory]
	[InlineData(Achievement.None)]
	[InlineData(Achievement.Waiting)]
	public async Task Importing_a_batch_whose_work_records_a_non_worked_achievement_throws_an_invariant_violation(
		Achievement achievement)
	{
		var sut = new JobCommands(CreateSeededPort());

		var act = () => sut.ImportSubtreeAsync(ImportRequestWith([
			WorkedNode(1, null, WorkFrom(TwoDaysAgo, OneDayAgo) with { Achievement = achievement }),
		]));

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("import-subtree-invalid-work-achievement");
	}

	[Fact]
	public async Task Importing_a_batch_whose_work_finishes_before_it_starts_throws_an_invariant_violation()
	{
		var sut = new JobCommands(CreateSeededPort());

		var act = () => sut.ImportSubtreeAsync(ImportRequestWith([
			WorkedNode(1, null, WorkFrom(OneDayAgo, TwoDaysAgo)),
		]));

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("import-subtree-invalid-work-interval");
	}

	[Fact]
	public async Task Importing_a_batch_that_completes_work_with_no_finish_instant_throws_an_invariant_violation()
	{
		var sut = new JobCommands(CreateSeededPort());

		var act = () => sut.ImportSubtreeAsync(ImportRequestWith([
			WorkedNode(1, null, WorkFrom(TwoDaysAgo, null) with { Achievement = Achievement.Success }),
		]));

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("import-subtree-unfinished-completed-work");
	}

	[Fact]
	public async Task Importing_a_batch_whose_work_starts_before_its_prerequisite_finishes_throws_an_invariant_violation()
	{
		var sut = new JobCommands(CreateSeededPort());

		// Node 2 requires node 1, but starts a day before node 1 finished -- chronologically
		// impossible history, even though replaying it in prerequisite order would satisfy the gate.
		var act = () => sut.ImportSubtreeAsync(ImportRequestWith([
			WorkedNode(1, null, WorkFrom(ThreeDaysAgo, OneDayAgo)),
			WorkedNode(2, null, WorkFrom(TwoDaysAgo, OneDayAgo), [1]),
		]));

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("import-subtree-work-precedes-prerequisite");
	}

	[Fact]
	public async Task Importing_a_batch_that_works_a_node_whose_prerequisite_never_succeeds_throws_an_invariant_violation()
	{
		var sut = new JobCommands(CreateSeededPort());

		// Node 1 is left open, so it never reaches Success and cannot satisfy node 2's gate.
		var act = () => sut.ImportSubtreeAsync(ImportRequestWith([
			WorkedNode(1, null, WorkFrom(ThreeDaysAgo, null) with { Achievement = Achievement.InProgress }),
			WorkedNode(2, null, WorkFrom(TwoDaysAgo, OneDayAgo), [1]),
		]));

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("import-subtree-work-blocked-by-prerequisite");
	}

	[Fact]
	public async Task Importing_a_batch_gates_work_on_a_prerequisite_branch_by_every_leaf_beneath_it()
	{
		var sut = new JobCommands(CreateSeededPort());

		// Node 3 requires branch 1, whose leaf 2 is cancelled -- so the branch never succeeds.
		var act = () => sut.ImportSubtreeAsync(ImportRequestWith([
			PlainNode(1),
			WorkedNode(2, 1, WorkFrom(ThreeDaysAgo, TwoDaysAgo) with { Achievement = Achievement.Cancelled }),
			WorkedNode(3, null, WorkFrom(OneDayAgo, OneDayAgo.Plus(Duration.FromHours(1))), [1]),
		]));

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("import-subtree-work-blocked-by-prerequisite");
	}

	[Fact]
	public async Task Importing_a_batch_carries_open_and_closed_work_through_to_the_port()
	{
		var port = CreateSeededPort();
		var sut = new JobCommands(port);

		var result = await sut.ImportSubtreeAsync(ImportRequestWith([
			WorkedNode(1, null, WorkFrom(ThreeDaysAgo, TwoDaysAgo)),
			WorkedNode(
				2, null, WorkFrom(OneDayAgo, null) with { Achievement = Achievement.InProgress },
				[1]),
		]));

		result.Nodes.Should().HaveCount(2);

		var imported = port.LastImportedNodes.ToDictionary(n => n.LocalId, n => n.LeafWork);
		imported[1]!.StartedAt.Should().Be(ThreeDaysAgo);
		imported[1]!.FinishedAt.Should().Be(TwoDaysAgo);
		imported[1]!.Achievement.Should().Be(Achievement.Success);
		imported[2]!.StartedAt.Should().Be(OneDayAgo);
		imported[2]!.FinishedAt.Should().BeNull();
		imported[2]!.Achievement.Should().Be(Achievement.InProgress);
	}
}
