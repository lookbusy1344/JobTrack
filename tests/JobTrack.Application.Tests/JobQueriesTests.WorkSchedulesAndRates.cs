namespace JobTrack.Application.Tests;

using System.Diagnostics;
using Abstractions;
using AwesomeAssertions;
using Domain.Costing;
using Domain.Hierarchy;
using NodaTime;
using TestSupport;

public sealed partial class JobQueriesTests
{
	[Fact]
	public async Task GetAwaitingProgressAsync_applies_the_owner_filter()
	{
		var owner = new AppUserId(10);
		var otherOwner = new AppUserId(11);
		var port = CreateSeededTree(owner, otherOwner, out _, out _, out var leafId);
		await port.AttachLeafWorkAsync(new() {
			Context = ContextFor(owner),
			JobNodeId = leafId,
		});
		var sut = CreateSut(port);

		var ownedByOther = await sut.GetAwaitingProgressAsync(
			new() {
				Context = ContextFor(owner),
				Ownership = OwnershipFilter.OwnedBy(otherOwner),
			});
		var ownedByOwner = await sut.GetAwaitingProgressAsync(
			new() {
				Context = ContextFor(owner),
				Ownership = OwnershipFilter.OwnedBy(owner),
			});

		ownedByOther.Select(e => e.Id).Should().ContainSingle().Which.Should().Be(leafId);
		ownedByOwner.Should().BeEmpty();
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_applies_the_subtree_filter()
	{
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, owner, out var rootId, out var branchId, out var leafId);
		await port.AttachLeafWorkAsync(new() {
			Context = ContextFor(owner),
			JobNodeId = leafId,
		});
		var otherBranchId = new JobNodeId(10);
		var otherLeafId = new JobNodeId(11);
		port.SeedNode(new() {
			Id = otherBranchId,
			ParentId = rootId,
			Kind = NodeKind.Branch,
			Description = "Other branch",
			PostedByUserId = owner,
			OwnerUserId = owner,
			Priority = Priority.Medium,
			PostedAt = port.NowToReturn,
			HasChildren = false,
			HasLeafWork = false,
			Version = 1,
		});
		port.SeedNode(new() {
			Id = otherLeafId,
			ParentId = otherBranchId,
			Kind = NodeKind.Leaf,
			Description = "Other leaf",
			PostedByUserId = owner,
			OwnerUserId = owner,
			Priority = Priority.Medium,
			PostedAt = port.NowToReturn,
			HasChildren = false,
			HasLeafWork = false,
			Version = 1,
		});
		await port.AttachLeafWorkAsync(new() {
			Context = ContextFor(owner),
			JobNodeId = otherLeafId,
		});
		var sut = CreateSut(port);

		var result = await sut.GetAwaitingProgressAsync(
			new() {
				Context = ContextFor(owner),
				SubtreeRootId = branchId,
			});

		result.Select(e => e.Id).Should().ContainSingle().Which.Should().Be(leafId);
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_applies_the_search_text_filter()
	{
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, owner, out _, out _, out var leafId);
		await port.AttachLeafWorkAsync(new() {
			Context = ContextFor(owner),
			JobNodeId = leafId,
		});
		var sut = CreateSut(port);

		var matching = await sut.GetAwaitingProgressAsync(new() {
			Context = ContextFor(owner),
			SearchText = "cabinets",
		});
		var nonMatching = await sut.GetAwaitingProgressAsync(new() {
			Context = ContextFor(owner),
			SearchText = "fence",
		});

		matching.Select(e => e.Id).Should().ContainSingle().Which.Should().Be(leafId);
		nonMatching.Should().BeEmpty();
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_pages_without_gaps_or_overlap_preserving_order()
	{
		const int leafCount = 5;
		var owner = new AppUserId(10);
		var port = new FakeJobNodeCommandPort();
		port.SeedRoles(owner, EmployeeRole.Administrator);
		var rootId = new JobNodeId(199);
		port.SeedNode(new() {
			Id = rootId,
			ParentId = null,
			Kind = NodeKind.Root,
			Description = "Root",
			PostedByUserId = owner,
			OwnerUserId = owner,
			Priority = Priority.Medium,
			PostedAt = port.NowToReturn,
			HasChildren = true,
			HasLeafWork = false,
			Version = 1,
		});
		var leafIds = new List<JobNodeId>();
		for (var index = 0; index < leafCount; ++index) {
			var leafId = new JobNodeId(200 + index);
			leafIds.Add(leafId);
			port.SeedNode(new() {
				Id = leafId,
				ParentId = rootId,
				Kind = NodeKind.Leaf,
				Description = $"Leaf {index}",
				PostedByUserId = owner,
				OwnerUserId = owner,
				Priority = Priority.Medium,
				PostedAt = port.NowToReturn,
				HasChildren = false,
				HasLeafWork = false,
				Version = 1,
			});
		}

		var sut = CreateSut(port);

		var firstPage = await sut.GetAwaitingProgressAsync(new() {
			Context = ContextFor(owner),
			Offset = 0,
			Limit = 2,
		});
		var secondPage = await sut.GetAwaitingProgressAsync(new() {
			Context = ContextFor(owner),
			Offset = 2,
			Limit = 2,
		});
		var thirdPage = await sut.GetAwaitingProgressAsync(new() {
			Context = ContextFor(owner),
			Offset = 4,
			Limit = 2,
		});
		var unpaged = await sut.GetAwaitingProgressAsync(new() {
			Context = ContextFor(owner),
		});

		firstPage.Should().HaveCount(2);
		secondPage.Should().HaveCount(2);
		thirdPage.Should().ContainSingle();
		var paged = firstPage.Concat(secondPage).Concat(thirdPage).Select(e => e.Id).ToArray();
		paged.Should().Equal(unpaged.Select(e => e.Id));
		paged.Distinct().Should().HaveCount(leafCount);
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_applies_a_bounded_default_when_limit_is_omitted()
	{
		var owner = new AppUserId(10);
		var port = CreateAwaitingProgressPort(owner, AwaitingProgressPaging.DefaultPageSize + 1);
		var sut = CreateSut(port);

		var result = await sut.GetAwaitingProgressAsync(new() {
			Context = ContextFor(owner),
		});

		result.Should().HaveCount(AwaitingProgressPaging.DefaultPageSize);
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_clamps_an_excessive_limit_to_the_maximum_page_size()
	{
		var owner = new AppUserId(10);
		var port = CreateAwaitingProgressPort(owner, AwaitingProgressPaging.MaxPageSize + 1);
		var sut = CreateSut(port);

		var result = await sut.GetAwaitingProgressAsync(new() {
			Context = ContextFor(owner),
			Limit = AwaitingProgressPaging.MaxPageSize + 1,
		});

		result.Should().HaveCount(AwaitingProgressPaging.MaxPageSize);
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_rejects_a_negative_offset()
	{
		var sut = CreateSut(new FakeJobNodeCommandPort());

		var act = () => sut.GetAwaitingProgressAsync(new() {
			Context = ContextFor(new(10)),
			Offset = -1,
		});

		await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_rejects_a_non_positive_explicit_limit()
	{
		var sut = CreateSut(new FakeJobNodeCommandPort());

		var act = () => sut.GetAwaitingProgressAsync(new() {
			Context = ContextFor(new(10)),
			Limit = 0,
		});

		await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_includes_reconciled_costs_for_returned_leaves_when_the_actor_may_view_them()
	{
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, new(11), out _, out _, out var leafId);
		await port.AttachLeafWorkAsync(new() {
			Context = ContextFor(owner),
			JobNodeId = leafId,
		});
		var costQueries = new FakeCostQueries();
		var allocatedDuration = AllocatedDuration.FromShare(new(Duration.FromMinutes(90).BclCompatibleTicks, 1));
		costQueries.SeedBulkCost(leafId, new(90m), allocatedDuration);
		var sut = CreateSut(port, costQueries);

		var result = await sut.GetAwaitingProgressAsync(new() {
			Context = ContextFor(owner),
		});

		result.Should().ContainSingle();
		result[0].Id.Should().Be(leafId);
		result[0].Cost.Should().Be(new Money(90m));
		result[0].AllocatedDuration.Should().Be(allocatedDuration);
		costQueries.GetBulkNodeCostsCallCount.Should().Be(1);
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_throws_when_the_subtree_root_does_not_exist()
	{
		var owner = new AppUserId(10);
		var port = CreateSeededTree(owner, new(11), out _, out _, out _);
		var sut = CreateSut(port);

		var act = () => sut.GetAwaitingProgressAsync(
			new() {
				Context = ContextFor(owner),
				SubtreeRootId = new JobNodeId(999),
			});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetAwaitingProgressAsync_rejects_a_null_request()
	{
		var sut = CreateSut(new FakeJobNodeCommandPort());

		var act = () => sut.GetAwaitingProgressAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task A_worker_can_view_their_own_sessions_on_a_leaf()
	{
		var worker = new AppUserId(20);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		port.SeedSession(new() {
			Id = new(1),
			LeafWorkId = leaf,
			WorkedByUserId = worker,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetLeafSessionsAsync(
			new() {
				Context = ContextFor(worker),
				LeafWorkId = leaf,
				WorkedByUserId = worker,
			});

		result.Should().ContainSingle(s => s.Id == new WorkSessionId(1));
	}

	// ADR 0041: recorded work is job data, which spec §7.3 makes viewable by every employee role,
	// so a Worker may now read another worker's sessions. Editing one remains gated separately by
	// WorkSessionAccessPolicy.CanManage's node-control rule, which this change does not touch.
	[Fact]
	public async Task A_worker_can_view_another_workers_sessions()
	{
		var worker = new AppUserId(20);
		var otherWorker = new AppUserId(21);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		port.SeedLeaf(leaf);
		port.SeedSession(new() {
			Id = new(7),
			LeafWorkId = leaf,
			WorkedByUserId = otherWorker,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetLeafSessionsAsync(
			new() {
				Context = ContextFor(worker),
				LeafWorkId = leaf,
				WorkedByUserId = otherWorker,
			});

		result.Should().ContainSingle(s => s.Id == new WorkSessionId(7));
	}

	[Fact]
	public async Task A_requester_cannot_view_sessions_at_all()
	{
		var requester = new AppUserId(20);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(requester, EmployeeRole.Requester);
		port.SeedLeaf(leaf);
		var sut = CreateSut(port);

		var act = () => sut.GetLeafSessionsAsync(
			new() {
				Context = ContextFor(requester),
				LeafWorkId = leaf,
			});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Omitting_the_worker_filter_returns_every_workers_sessions_on_the_leaf()
	{
		var worker = new AppUserId(20);
		var otherWorker = new AppUserId(21);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		port.SeedLeaf(leaf);
		port.SeedSession(new() {
			Id = new(1),
			LeafWorkId = leaf,
			WorkedByUserId = worker,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			Version = 1,
		});
		port.SeedSession(new() {
			Id = new(2),
			LeafWorkId = leaf,
			WorkedByUserId = otherWorker,
			StartedAt = Instant.FromUtc(2026, 1, 1, 11, 0),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 11, 0),
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetLeafSessionsAsync(
			new() {
				Context = ContextFor(worker),
				LeafWorkId = leaf,
			});

		result.Should().HaveCount(2);
		// Most-recent-first ordering must hold across the union, not just within one worker's bucket.
		result.Select(s => s.Id).Should().ContainInOrder(new WorkSessionId(2), new WorkSessionId(1));
	}

	[Fact]
	public async Task A_job_manager_can_view_any_workers_sessions()
	{
		var manager = new AppUserId(20);
		var worker = new AppUserId(21);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(manager, EmployeeRole.JobManager);
		port.SeedSession(new() {
			Id = new(1),
			LeafWorkId = leaf,
			WorkedByUserId = worker,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetLeafSessionsAsync(
			new() {
				Context = ContextFor(manager),
				LeafWorkId = leaf,
				WorkedByUserId = worker,
			});

		result.Should().ContainSingle(s => s.Id == new WorkSessionId(1));
	}

	[Fact]
	public async Task Querying_sessions_for_a_nonexistent_leaf_throws_not_found()
	{
		var worker = new AppUserId(20);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		var sut = CreateSut(port);

		var act = () => sut.GetLeafSessionsAsync(
			new() {
				Context = ContextFor(worker),
				LeafWorkId = new(999),
				WorkedByUserId = worker,
			});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetLeafSessionsAsync_rejects_a_null_request()
	{
		var sut = CreateSut(new FakeWorkSessionQueryPort());

		var act = () => sut.GetLeafSessionsAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task A_worker_can_view_their_own_active_sessions_across_leaves()
	{
		var worker = new AppUserId(20);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		port.SeedSession(new() {
			Id = new(1),
			LeafWorkId = leaf,
			WorkedByUserId = worker,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetActiveSessionsAsync(
			new() {
				Context = ContextFor(worker),
				LeafWorkIds = [leaf],
			});

		result.Should().ContainSingle(s => s.Id == new WorkSessionId(1));
	}

	[Fact]
	public async Task GetActiveSessionsAsync_does_not_return_a_finished_session()
	{
		var worker = new AppUserId(20);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		port.SeedSession(new() {
			Id = new(1),
			LeafWorkId = leaf,
			WorkedByUserId = worker,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			FinishedAt = Instant.FromUtc(2026, 1, 1, 10, 0),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 10, 0),
			Version = 2,
		});
		var sut = CreateSut(port);

		var result = await sut.GetActiveSessionsAsync(
			new() {
				Context = ContextFor(worker),
				LeafWorkIds = [leaf],
			});

		result.Should().BeEmpty();
	}

	[Fact]
	public async Task GetActiveSessionsAsync_throws_when_the_actor_holds_no_manageable_role()
	{
		var worker = new AppUserId(20);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(worker);
		var sut = CreateSut(port);

		var act = () => sut.GetActiveSessionsAsync(
			new() {
				Context = ContextFor(worker),
				LeafWorkIds = [],
			});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	/// <summary>
	///     ADR 0041 already made viewing recorded work unqualified for any baseline employee role; the
	///     same "recorded work is job data" reasoning applies to this batch, not only
	///     <see cref="GetLeafSessionsAsync" />'s history read (browse-sessions plan §2.4: the Active
	///     column must never collapse away another worker's session for the common plain-Worker
	///     viewer). <see cref="Domain.Authorization.WorkSessionAccessPolicy.CanManage" /> still gates
	///     whether this worker may finish that session — it plays no role in whether they can see it.
	/// </summary>
	[Fact]
	public async Task GetActiveSessionsAsync_includes_another_workers_session_for_a_plain_worker_who_does_not_control_the_leaf()
	{
		var worker = new AppUserId(20);
		var otherWorker = new AppUserId(21);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		port.SeedSession(new() {
			Id = new(1),
			LeafWorkId = leaf,
			WorkedByUserId = otherWorker,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetActiveSessionsAsync(
			new() {
				Context = ContextFor(worker),
				LeafWorkIds = [leaf],
			});

		result.Should().ContainSingle(s => s.Id == new WorkSessionId(1));
	}

	/// <summary>
	///     Administrator/JobManager may finish any leaf's session unconditionally
	///     (<see cref="Domain.Authorization.WorkSessionAccessPolicy.CanManage" />, ADR 0032), so this
	///     read surfaces another worker's active session to them too -- otherwise the dashboard would
	///     offer a Start button for work that is already in progress.
	/// </summary>
	[Fact]
	public async Task GetActiveSessionsAsync_includes_another_workers_session_for_an_administrator()
	{
		var administrator = new AppUserId(20);
		var worker = new AppUserId(21);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(administrator, EmployeeRole.Administrator);
		port.SeedSession(new() {
			Id = new(1),
			LeafWorkId = leaf,
			WorkedByUserId = worker,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetActiveSessionsAsync(
			new() {
				Context = ContextFor(administrator),
				LeafWorkIds = [leaf],
			});

		result.Should().ContainSingle(s => s.Id == new WorkSessionId(1));
	}

	[Fact]
	public async Task GetActiveSessionsAsync_rejects_a_null_request()
	{
		var sut = CreateSut(new FakeWorkSessionQueryPort());

		var act = () => sut.GetActiveSessionsAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task GetSessionManageCapabilitiesAsync_reports_true_for_an_administrator_regardless_of_ownership()
	{
		var administrator = new AppUserId(20);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(administrator, EmployeeRole.Administrator);
		var sut = CreateSut(port);

		var result = await sut.GetSessionManageCapabilitiesAsync(
			new() {
				Context = ContextFor(administrator),
				LeafWorkIds = [leaf],
			});

		result.Should().ContainSingle().Which.Should().BeEquivalentTo(new
		{
			LeafWorkId = leaf,
			CanManage = true,
		});
	}

	[Fact]
	public async Task GetSessionManageCapabilitiesAsync_reports_true_for_a_worker_who_controls_the_leaf()
	{
		var worker = new AppUserId(20);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		port.SeedControl(worker, leaf);
		var sut = CreateSut(port);

		var result = await sut.GetSessionManageCapabilitiesAsync(
			new() {
				Context = ContextFor(worker),
				LeafWorkIds = [leaf],
			});

		result.Should().ContainSingle().Which.CanManage.Should().BeTrue();
	}

	[Fact]
	public async Task GetSessionManageCapabilitiesAsync_reports_false_for_a_worker_who_does_not_control_the_leaf()
	{
		var worker = new AppUserId(20);
		var leaf = new JobNodeId(30);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		var sut = CreateSut(port);

		var result = await sut.GetSessionManageCapabilitiesAsync(
			new() {
				Context = ContextFor(worker),
				LeafWorkIds = [leaf],
			});

		result.Should().ContainSingle().Which.CanManage.Should().BeFalse();
	}

	[Fact]
	public async Task GetSessionManageCapabilitiesAsync_reports_one_result_per_requested_leaf_regardless_of_control()
	{
		var worker = new AppUserId(20);
		var controlledLeaf = new JobNodeId(30);
		var uncontrolledLeaf = new JobNodeId(31);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		port.SeedControl(worker, controlledLeaf);
		var sut = CreateSut(port);

		var result = await sut.GetSessionManageCapabilitiesAsync(
			new() {
				Context = ContextFor(worker),
				LeafWorkIds = [controlledLeaf, uncontrolledLeaf],
			});

		result.Should().HaveCount(2);
		result.Single(r => r.LeafWorkId == controlledLeaf).CanManage.Should().BeTrue();
		result.Single(r => r.LeafWorkId == uncontrolledLeaf).CanManage.Should().BeFalse();
	}

	[Fact]
	public async Task GetSessionManageCapabilitiesAsync_with_no_leaves_authenticates_the_actor_and_returns_an_empty_result()
	{
		var worker = new AppUserId(20);
		var port = new FakeWorkSessionQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		var sut = CreateSut(port);

		var result = await sut.GetSessionManageCapabilitiesAsync(
			new() {
				Context = ContextFor(worker),
				LeafWorkIds = [],
			});

		result.Should().BeEmpty();
	}

	[Fact]
	public async Task GetSessionManageCapabilitiesAsync_with_no_leaves_rejects_a_nonexistent_actor()
	{
		var sut = CreateSut(new FakeWorkSessionQueryPort());

		var act = () => sut.GetSessionManageCapabilitiesAsync(
			new() {
				Context = ContextFor(new(21)),
				LeafWorkIds = [],
			});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetSessionManageCapabilitiesAsync_rejects_a_null_request()
	{
		var sut = CreateSut(new FakeWorkSessionQueryPort());

		var act = () => sut.GetSessionManageCapabilitiesAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task GetLeafWorkAsync_returns_the_leafs_current_achievement()
	{
		var actor = new AppUserId(20);
		var leaf = new JobNodeId(30);
		var port = new FakeLeafWorkQueryPort();
		port.Seed(new() {
			JobNodeId = leaf,
			Achievement = Achievement.InProgress,
			ChangedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetLeafWorkAsync(new() {
			Context = ContextFor(actor),
			JobNodeId = leaf,
		});

		result.Achievement.Should().Be(Achievement.InProgress);
	}

	[Fact]
	public async Task GetLeafWorkAsync_throws_when_no_leaf_work_is_attached()
	{
		var actor = new AppUserId(20);
		var sut = CreateSut(new FakeLeafWorkQueryPort());

		var act = () => sut.GetLeafWorkAsync(new() {
			Context = ContextFor(actor),
			JobNodeId = new(999),
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetLeafWorkAsync_rejects_a_null_request()
	{
		var sut = CreateSut(new FakeLeafWorkQueryPort());

		var act = () => sut.GetLeafWorkAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task GetPrerequisitesAsync_returns_edges_in_either_direction()
	{
		var actor = new AppUserId(20);
		var required = new JobNodeId(30);
		var dependent = new JobNodeId(31);
		var port = new FakePrerequisiteQueryPort();
		port.SeedEdge(new(required, dependent));
		var sut = CreateSut(port);

		var requiredSide = await sut.GetPrerequisitesAsync(new() {
			Context = ContextFor(actor),
			NodeId = required,
		});
		var dependentSide = await sut.GetPrerequisitesAsync(new() {
			Context = ContextFor(actor),
			NodeId = dependent,
		});

		requiredSide.Should().ContainSingle(e => e.RequiredJobId == required && e.DependentJobId == dependent);
		dependentSide.Should().ContainSingle(e => e.RequiredJobId == required && e.DependentJobId == dependent);
	}

	[Fact]
	public async Task GetPrerequisitesAsync_throws_for_a_nonexistent_node()
	{
		var actor = new AppUserId(20);
		var sut = CreateSut(new FakePrerequisiteQueryPort());

		var act = () => sut.GetPrerequisitesAsync(new() {
			Context = ContextFor(actor),
			NodeId = new(999),
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetPrerequisitesAsync_rejects_a_null_request()
	{
		var sut = CreateSut(new FakePrerequisiteQueryPort());

		var act = () => sut.GetPrerequisitesAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task A_worker_can_view_their_own_schedule()
	{
		var worker = new AppUserId(20);
		var port = new FakeScheduleQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		port.SeedVersion(new() {
			Id = new(1),
			UserId = worker,
			Schedule = new(
				DateTimeZoneProviders.Tzdb["Europe/London"], new(2026, 1, 1), null,
				[new(IsoDayOfWeek.Monday, new(9, 0), new(17, 0))]),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetScheduleAsync(new() {
			Context = ContextFor(worker),
			UserId = worker,
		});

		result.Versions.Should().ContainSingle();
		result.Exceptions.Should().BeEmpty();
	}

	[Fact]
	public async Task A_worker_cannot_view_another_workers_schedule()
	{
		var worker = new AppUserId(20);
		var otherWorker = new AppUserId(21);
		var port = new FakeScheduleQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		port.SeedEmployee(otherWorker);
		var sut = CreateSut(port);

		var act = () => sut.GetScheduleAsync(new() {
			Context = ContextFor(worker),
			UserId = otherWorker,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task An_administrator_can_view_another_employees_schedule()
	{
		var administrator = new AppUserId(20);
		var worker = new AppUserId(21);
		var port = new FakeScheduleQueryPort();
		port.SeedRoles(administrator, EmployeeRole.Administrator);
		port.SeedEmployee(worker);
		var sut = CreateSut(port);

		var result = await sut.GetScheduleAsync(new() {
			Context = ContextFor(administrator),
			UserId = worker,
		});

		result.Versions.Should().BeEmpty();
	}

	[Fact]
	public async Task Querying_a_nonexistent_employees_schedule_throws_not_found()
	{
		var administrator = new AppUserId(20);
		var port = new FakeScheduleQueryPort();
		port.SeedRoles(administrator, EmployeeRole.Administrator);
		var sut = CreateSut(port);

		var act = () => sut.GetScheduleAsync(new() {
			Context = ContextFor(administrator),
			UserId = new(999),
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetScheduleAsync_rejects_a_null_request()
	{
		var sut = CreateSut(new FakeScheduleQueryPort());

		var act = () => sut.GetScheduleAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task An_administrator_can_view_an_employees_rates()
	{
		var administrator = new AppUserId(20);
		var worker = new AppUserId(21);
		var port = new FakeRateQueryPort();
		port.SeedRoles(administrator, EmployeeRole.Administrator);
		port.SeedUserCostRate(new() {
			Id = new(1),
			UserId = worker,
			Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
			ChangedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
			Version = 1,
		});
		var sut = CreateSut(port);

		var result = await sut.GetRatesAsync(new() {
			Context = ContextFor(administrator),
			UserId = worker,
		});

		result.UserCostRates.Should().ContainSingle();
		result.NodeRateOverrides.Should().BeEmpty();
	}

	[Fact]
	public async Task A_cost_viewer_can_view_an_employees_rates()
	{
		var costViewer = new AppUserId(20);
		var worker = new AppUserId(21);
		var port = new FakeRateQueryPort();
		port.SeedRoles(costViewer, EmployeeRole.CostViewer);
		port.SeedEmployee(worker);
		var sut = CreateSut(port);

		var result = await sut.GetRatesAsync(new() {
			Context = ContextFor(costViewer),
			UserId = worker,
		});

		result.UserCostRates.Should().BeEmpty();
	}

	[Fact]
	public async Task A_rate_manager_without_cost_visibility_cannot_view_an_employees_rates()
	{
		var rateManager = new AppUserId(20);
		var worker = new AppUserId(21);
		var port = new FakeRateQueryPort();
		port.SeedRoles(rateManager, EmployeeRole.RateManager);
		port.SeedEmployee(worker);
		var sut = CreateSut(port);

		var act = () => sut.GetRatesAsync(new() {
			Context = ContextFor(rateManager),
			UserId = worker,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task A_worker_cannot_view_their_own_rates()
	{
		var worker = new AppUserId(20);
		var port = new FakeRateQueryPort();
		port.SeedRoles(worker, EmployeeRole.Worker);
		port.SeedEmployee(worker);
		var sut = CreateSut(port);

		var act = () => sut.GetRatesAsync(new() {
			Context = ContextFor(worker),
			UserId = worker,
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task Querying_a_nonexistent_employees_rates_throws_not_found()
	{
		var administrator = new AppUserId(20);
		var port = new FakeRateQueryPort();
		port.SeedRoles(administrator, EmployeeRole.Administrator);
		var sut = CreateSut(port);

		var act = () => sut.GetRatesAsync(new() {
			Context = ContextFor(administrator),
			UserId = new(999),
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetRatesAsync_rejects_a_null_request()
	{
		var sut = CreateSut(new FakeRateQueryPort());

		var act = () => sut.GetRatesAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	private static (FakeJobNodeCommandPort NodePort, FakeWorkSessionQueryPort SessionPort, FakeLeafWorkQueryPort LeafWorkPort,
		FakePrerequisiteQueryPort PrerequisitePort) CreateLeafWorkPageFakes()
	{
		var nodePort = new FakeJobNodeCommandPort();
		nodePort.SeedRoles(AdministratorId, EmployeeRole.Administrator);
		nodePort.SeedRoles(WorkerId, EmployeeRole.Worker);
		nodePort.SeedRoles(OtherWorkerId, EmployeeRole.Worker);
		nodePort.SeedNode(new() {
			Id = RootIdForWorkPage,
			ParentId = null,
			Kind = NodeKind.Root,
			Description = "Root",
			PostedByUserId = AdministratorId,
			OwnerUserId = AdministratorId,
			Priority = Priority.Medium,
			PostedAt = nodePort.NowToReturn,
			HasChildren = true,
			HasLeafWork = false,
			Version = 1,
		});
		nodePort.SeedNode(new() {
			Id = LeafIdForWorkPage,
			ParentId = RootIdForWorkPage,
			Kind = NodeKind.Leaf,
			Description = "Fit cabinets",
			PostedByUserId = AdministratorId,
			OwnerUserId = WorkerId,
			Priority = Priority.Medium,
			PostedAt = nodePort.NowToReturn,
			HasChildren = false,
			HasLeafWork = true,
			Version = 1,
		});
		// FakeJobNodeCommandPort derives HasLeafWork from its own internal _leafWork set (mirroring
		// AttachLeafWorkAsync's structural-fact tracking), not from the seeded JobNodeResult field
		// above -- SetLeafWork keeps that internal fact in sync with the leaf being tested.
		nodePort.SetLeafWork(
			new() {
				JobNodeId = LeafIdForWorkPage,
				Achievement = Achievement.Waiting,
				ChangedAt = nodePort.NowToReturn,
				Version = 1,
			});

		var sessionPort = new FakeWorkSessionQueryPort();
		sessionPort.SeedRoles(AdministratorId, EmployeeRole.Administrator);
		sessionPort.SeedRoles(WorkerId, EmployeeRole.Worker);
		sessionPort.SeedRoles(OtherWorkerId, EmployeeRole.Worker);
		sessionPort.SeedLeaf(LeafIdForWorkPage);
		sessionPort.SeedControl(WorkerId, LeafIdForWorkPage);

		var leafWorkPort = new FakeLeafWorkQueryPort();
		var prerequisitePort = new FakePrerequisiteQueryPort();
		prerequisitePort.SeedNode(LeafIdForWorkPage);

		return (nodePort, sessionPort, leafWorkPort, prerequisitePort);
	}

	private static JobQueries CreateLeafWorkPageSut(
		FakeJobNodeCommandPort nodePort, FakeWorkSessionQueryPort sessionPort, FakeLeafWorkQueryPort leafWorkPort,
		FakePrerequisiteQueryPort prerequisitePort) =>
		new(EmployeePortMirroring(nodePort), nodePort, nodePort, nodePort, sessionPort, leafWorkPort, prerequisitePort,
			new FakeScheduleQueryPort(), new FakeRateQueryPort(), new FakeCostQueries(), SystemClock.Instance);

	[Fact]
	public async Task The_leaf_work_page_reports_two_concurrent_active_sessions_without_collapsing_them()
	{
		var (nodePort, sessionPort, leafWorkPort, prerequisitePort) = CreateLeafWorkPageFakes();
		leafWorkPort.Seed(new() {
			JobNodeId = LeafIdForWorkPage,
			Achievement = Achievement.InProgress,
			ChangedAt = nodePort.NowToReturn,
			Version = 2,
		});
		sessionPort.SeedSession(new() {
			Id = new(1),
			LeafWorkId = LeafIdForWorkPage,
			WorkedByUserId = WorkerId,
			StartedAt = nodePort.NowToReturn,
			ChangedAt = nodePort.NowToReturn,
			Version = 1,
		});
		sessionPort.SeedSession(new() {
			Id = new(2),
			LeafWorkId = LeafIdForWorkPage,
			WorkedByUserId = OtherWorkerId,
			StartedAt = nodePort.NowToReturn,
			ChangedAt = nodePort.NowToReturn,
			Version = 1,
		});
		var sut = CreateLeafWorkPageSut(nodePort, sessionPort, leafWorkPort, prerequisitePort);

		var result = await sut.GetLeafWorkPageAsync(new() {
			Context = ContextFor(WorkerId),
			JobNodeId = LeafIdForWorkPage,
		});

		result.HasLeafWork.Should().BeTrue();
		result.Achievement.Should().Be(Achievement.InProgress);
		result.ActiveSessions.Should().HaveCount(2);
		result.ActiveSessions.Select(s => s.WorkedByUserId).Should().BeEquivalentTo([WorkerId, OtherWorkerId]);
	}

	[Fact]
	public async Task The_leaf_work_page_grants_complete_and_reopen_for_others_to_a_controlling_owner()
	{
		var (nodePort, sessionPort, leafWorkPort, prerequisitePort) = CreateLeafWorkPageFakes();
		leafWorkPort.Seed(new() {
			JobNodeId = LeafIdForWorkPage,
			Achievement = Achievement.Unsuccessful,
			ChangedAt = nodePort.NowToReturn,
			Version = 2,
		});
		var sut = CreateLeafWorkPageSut(nodePort, sessionPort, leafWorkPort, prerequisitePort);

		var result = await sut.GetLeafWorkPageAsync(new() {
			Context = ContextFor(WorkerId),
			JobNodeId = LeafIdForWorkPage,
		});

		result.ActorControlsNode.Should().BeTrue();
		result.CanComplete.Should().BeTrue();
		result.CanReopenAndStartForSelf.Should().BeTrue();
		result.CanReopenAndStartForOthers.Should().BeTrue();
		result.CanReopenWithoutStarting.Should().BeFalse("node control alone does not grant elevated reopen-only correction authority");
	}

	[Fact]
	public async Task The_leaf_work_page_grants_reopen_without_starting_only_to_an_elevated_actor()
	{
		var (nodePort, sessionPort, leafWorkPort, prerequisitePort) = CreateLeafWorkPageFakes();
		leafWorkPort.Seed(new() {
			JobNodeId = LeafIdForWorkPage,
			Achievement = Achievement.Unsuccessful,
			ChangedAt = nodePort.NowToReturn,
			Version = 2,
		});
		var sut = CreateLeafWorkPageSut(nodePort, sessionPort, leafWorkPort, prerequisitePort);

		var result = await sut.GetLeafWorkPageAsync(new() {
			Context = ContextFor(AdministratorId),
			JobNodeId = LeafIdForWorkPage,
		});

		result.CanReopenWithoutStarting.Should().BeTrue();
	}

	[Fact]
	public async Task The_leaf_work_page_grants_reopen_for_self_only_to_a_prior_participant_with_no_control()
	{
		var (nodePort, sessionPort, leafWorkPort, prerequisitePort) = CreateLeafWorkPageFakes();
		leafWorkPort.Seed(new() {
			JobNodeId = LeafIdForWorkPage,
			Achievement = Achievement.Unsuccessful,
			ChangedAt = nodePort.NowToReturn,
			Version = 2,
		});
		sessionPort.SeedSession(new() {
			Id = new(1),
			LeafWorkId = LeafIdForWorkPage,
			WorkedByUserId = OtherWorkerId,
			StartedAt = nodePort.NowToReturn,
			FinishedAt = nodePort.NowToReturn,
			ChangedAt = nodePort.NowToReturn,
			Version = 2,
		});
		var sut = CreateLeafWorkPageSut(nodePort, sessionPort, leafWorkPort, prerequisitePort);

		var result = await sut.GetLeafWorkPageAsync(new() {
			Context = ContextFor(OtherWorkerId),
			JobNodeId = LeafIdForWorkPage,
		});

		result.ActorControlsNode.Should().BeFalse();
		result.ActorParticipatedPreviously.Should().BeTrue();
		result.CanComplete.Should().BeFalse();
		result.CanReopenAndStartForSelf.Should().BeTrue();
		result.CanReopenAndStartForOthers.Should().BeFalse();
		result.CanReopenWithoutStarting.Should().BeFalse();
	}

	[Fact]
	public async Task The_leaf_work_page_grants_no_reopen_authority_to_a_non_participant_non_controller()
	{
		var (nodePort, sessionPort, leafWorkPort, prerequisitePort) = CreateLeafWorkPageFakes();
		leafWorkPort.Seed(new() {
			JobNodeId = LeafIdForWorkPage,
			Achievement = Achievement.Unsuccessful,
			ChangedAt = nodePort.NowToReturn,
			Version = 2,
		});
		var sut = CreateLeafWorkPageSut(nodePort, sessionPort, leafWorkPort, prerequisitePort);

		var result = await sut.GetLeafWorkPageAsync(new() {
			Context = ContextFor(OtherWorkerId),
			JobNodeId = LeafIdForWorkPage,
		});

		result.CanReopenAndStartForSelf.Should().BeFalse();
		result.CanReopenAndStartForOthers.Should().BeFalse();
	}

	[Fact]
	public async Task The_leaf_work_page_counts_direct_dependents_of_a_successful_leaf()
	{
		var (nodePort, sessionPort, leafWorkPort, prerequisitePort) = CreateLeafWorkPageFakes();
		leafWorkPort.Seed(new() {
			JobNodeId = LeafIdForWorkPage,
			Achievement = Achievement.Success,
			ChangedAt = nodePort.NowToReturn,
			Version = 2,
		});
		var dependentId = new JobNodeId(3);
		prerequisitePort.SeedNode(dependentId);
		prerequisitePort.SeedEdge(new(LeafIdForWorkPage, dependentId));
		var sut = CreateLeafWorkPageSut(nodePort, sessionPort, leafWorkPort, prerequisitePort);

		var result = await sut.GetLeafWorkPageAsync(new() {
			Context = ContextFor(WorkerId),
			JobNodeId = LeafIdForWorkPage,
		});

		result.DirectDependentCount.Should().Be(1);
		prerequisitePort.CountDirectDependentsCallCount.Should().Be(1);
		prerequisitePort.GetPrerequisitesCallCount.Should().Be(0, "the bounded page projection must not materialize every touching edge");
	}

	[Fact]
	public async Task The_leaf_work_page_reports_a_dependent_holding_an_active_session_on_a_successful_leaf()
	{
		var (nodePort, sessionPort, leafWorkPort, prerequisitePort) = CreateLeafWorkPageFakes();
		leafWorkPort.Seed(new() {
			JobNodeId = LeafIdForWorkPage,
			Achievement = Achievement.Success,
			ChangedAt = nodePort.NowToReturn,
			Version = 2,
		});
		var dependentId = new JobNodeId(3);
		prerequisitePort.SeedNode(dependentId);
		prerequisitePort.SeedEdge(new(LeafIdForWorkPage, dependentId));
		prerequisitePort.SeedActiveDependentWork(LeafIdForWorkPage);
		var sut = CreateLeafWorkPageSut(nodePort, sessionPort, leafWorkPort, prerequisitePort);

		var result = await sut.GetLeafWorkPageAsync(new() {
			Context = ContextFor(WorkerId),
			JobNodeId = LeafIdForWorkPage,
		});

		result.HasActiveDependentWork.Should().BeTrue();
	}

	[Fact]
	public async Task The_leaf_work_page_does_not_ask_about_dependent_work_for_a_leaf_that_cannot_be_reopened_from_success()
	{
		var (nodePort, sessionPort, leafWorkPort, prerequisitePort) = CreateLeafWorkPageFakes();
		leafWorkPort.Seed(new() {
			JobNodeId = LeafIdForWorkPage,
			Achievement = Achievement.InProgress,
			ChangedAt = nodePort.NowToReturn,
			Version = 2,
		});
		var dependentId = new JobNodeId(3);
		prerequisitePort.SeedNode(dependentId);
		prerequisitePort.SeedEdge(new(LeafIdForWorkPage, dependentId));
		prerequisitePort.SeedActiveDependentWork(LeafIdForWorkPage);
		var sut = CreateLeafWorkPageSut(nodePort, sessionPort, leafWorkPort, prerequisitePort);

		var result = await sut.GetLeafWorkPageAsync(new() {
			Context = ContextFor(WorkerId),
			JobNodeId = LeafIdForWorkPage,
		});

		result.HasActiveDependentWork.Should().BeFalse("only Success can be reopened into a readiness regression");
		prerequisitePort.HasActiveDependentWorkCallCount.Should().Be(0, "the extra round trip is spent only where the warning could fire");
	}

	[Fact]
	public async Task The_leaf_work_page_reports_no_leaf_work_for_a_bare_leaf_without_throwing()
	{
		var (nodePort, sessionPort, leafWorkPort, prerequisitePort) = CreateLeafWorkPageFakes();
		var bareLeafId = new JobNodeId(4);
		nodePort.SeedNode(new() {
			Id = bareLeafId,
			ParentId = RootIdForWorkPage,
			Kind = NodeKind.Leaf,
			Description = "Bare leaf",
			PostedByUserId = AdministratorId,
			OwnerUserId = WorkerId,
			Priority = Priority.Medium,
			PostedAt = nodePort.NowToReturn,
			HasChildren = false,
			HasLeafWork = false,
			Version = 1,
		});
		var sut = CreateLeafWorkPageSut(nodePort, sessionPort, leafWorkPort, prerequisitePort);

		var result = await sut.GetLeafWorkPageAsync(new() {
			Context = ContextFor(WorkerId),
			JobNodeId = bareLeafId,
		});

		result.HasLeafWork.Should().BeFalse();
		result.Achievement.Should().BeNull();
		result.ActiveSessions.Should().BeEmpty();
		result.CanComplete.Should().BeFalse();
	}

	[Fact]
	public async Task Querying_the_leaf_work_page_for_a_nonexistent_node_throws_not_found()
	{
		var (nodePort, sessionPort, leafWorkPort, prerequisitePort) = CreateLeafWorkPageFakes();
		var sut = CreateLeafWorkPageSut(nodePort, sessionPort, leafWorkPort, prerequisitePort);

		var act = () => sut.GetLeafWorkPageAsync(new() {
			Context = ContextFor(WorkerId),
			JobNodeId = new(999),
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetLeafWorkPageAsync_rejects_a_null_request()
	{
		var (nodePort, sessionPort, leafWorkPort, prerequisitePort) = CreateLeafWorkPageFakes();
		var sut = CreateLeafWorkPageSut(nodePort, sessionPort, leafWorkPort, prerequisitePort);

		var act = () => sut.GetLeafWorkPageAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	/// <summary>
	///     Remediation plan §2.4 step 1: a representative table of read-capability classes across the
	///     general job/work query surface (structural, listing, leaf, and edge reads), rather than one
	///     near-identical test per every public <c>IJobQueries</c> member.
	/// </summary>
	public static TheoryData<string> JobDataBrowseCallNames() =>
		new() {
			"GetJobNodeAsync",
			"GetAwaitingProgressAsync",
			"GetLeafWorkAsync",
			"GetPrerequisitesAsync",
			"GetEmployeeDirectoryAsync",
		};

	private static Task InvokeJobDataBrowseCallAsync(string caseName, JobQueries sut, CommandContext context) =>
		caseName switch {
			"GetJobNodeAsync" => sut.GetJobNodeAsync(new() {
				Context = context,
				NodeId = null,
			}),
			"GetAwaitingProgressAsync" => sut.GetAwaitingProgressAsync(new() {
				Context = context,
			}),
			"GetLeafWorkAsync" => sut.GetLeafWorkAsync(new() {
				Context = context,
				JobNodeId = new(1),
			}),
			"GetPrerequisitesAsync" => sut.GetPrerequisitesAsync(new() {
				Context = context,
				NodeId = new(1),
			}),
			"GetEmployeeDirectoryAsync" => sut.GetEmployeeDirectoryAsync(new() {
				Context = context,
			}),
			_ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown job-data browse case."),
		};

	[Theory]
	[MemberData(nameof(JobDataBrowseCallNames))]
	public async Task A_requester_only_actor_may_not_browse_the_general_job_data_query_surface(string caseName)
	{
		var actor = new AppUserId(9001);
		var employeeQueryPort = new FakeEmployeeQueryPort();
		employeeQueryPort.SeedRoles(actor, [EmployeeRole.Requester]);
		var sut = CreateSut(employeeQueryPort);

		var act = () => InvokeJobDataBrowseCallAsync(caseName, sut, ContextFor(actor));

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Theory]
	[MemberData(nameof(JobDataBrowseCallNames))]
	public async Task A_requester_with_an_operational_role_may_not_browse_the_general_job_data_query_surface(string caseName)
	{
		var actor = new AppUserId(9005);
		var employeeQueryPort = new FakeEmployeeQueryPort();
		employeeQueryPort.SeedRoles(actor, [EmployeeRole.Requester, EmployeeRole.Worker]);
		var sut = CreateSut(employeeQueryPort);

		var act = () => InvokeJobDataBrowseCallAsync(caseName, sut, ContextFor(actor));

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Theory]
	[MemberData(nameof(JobDataBrowseCallNames))]
	public async Task A_role_less_actor_may_not_browse_the_general_job_data_query_surface(string caseName)
	{
		var actor = new AppUserId(9002);
		var employeeQueryPort = new FakeEmployeeQueryPort();
		employeeQueryPort.SeedRoles(actor, []);
		var sut = CreateSut(employeeQueryPort);

		var act = () => InvokeJobDataBrowseCallAsync(caseName, sut, ContextFor(actor));

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Theory]
	[MemberData(nameof(JobDataBrowseCallNames))]
	public async Task A_nonexistent_actor_may_not_browse_the_general_job_data_query_surface(string caseName)
	{
		var actor = new AppUserId(9003);
		var sut = CreateSut(new FakeEmployeeQueryPort());

		var act = () => InvokeJobDataBrowseCallAsync(caseName, sut, ContextFor(actor));

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetAllEmployeesAsync_requires_administrator()
	{
		var actor = new AppUserId(9004);
		var employeeQueryPort = new FakeEmployeeQueryPort();
		employeeQueryPort.SeedRoles(actor, [EmployeeRole.JobManager]);
		var sut = CreateSut(employeeQueryPort);

		var act = () => sut.GetAllEmployeesAsync(new() {
			Context = ContextFor(actor),
		});

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task GetAllEmployeesAsync_throws_for_a_nonexistent_actor()
	{
		var actor = new AppUserId(9005);
		var sut = CreateSut(new FakeEmployeeQueryPort());

		var act = () => sut.GetAllEmployeesAsync(new() {
			Context = ContextFor(actor),
		});

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}
}
