namespace JobTrack.Application.Tests;

using Abstractions;
using AwesomeAssertions;
using Domain.Intervals;
using NodaTime;

public sealed class CostQueriesTests
{
	private static readonly AppUserId CostViewerId = new(1);
	private static readonly AppUserId WorkerId = new(2);
	private static readonly JobNodeId RootId = new(1);
	private static readonly JobNodeId BranchId = new(2);
	private static readonly JobNodeId LeafId = new(3);
	private static readonly JobNodeId OtherLeafId = new(4);
	private static readonly WorkSessionId Session1 = new(1);
	private static readonly WorkSessionId Session2 = new(2);

	private static readonly WorkInterval FullDay = new(At(0), At(24));

	private static Instant At(int hour) => hour == 24 ? Instant.FromUtc(2026, 1, 2, 0, 0) : Instant.FromUtc(2026, 1, 1, hour, 0);

	private static FakeCostQueryPort CreatePortWithNodes()
	{
		var port = new FakeCostQueryPort();
		port.SeedRoles(CostViewerId, EmployeeRole.CostViewer);
		port.SeedRoles(WorkerId, EmployeeRole.Worker);

		port.SeedNode(new(RootId, null, [BranchId, OtherLeafId], null));
		port.SeedNode(new(BranchId, RootId, [LeafId], null));
		port.SeedNode(new(LeafId, BranchId, [], Achievement.InProgress));
		port.SeedNode(new(OtherLeafId, RootId, [], Achievement.InProgress));

		return port;
	}

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	[Fact]
	public async Task A_cost_viewer_can_calculate_cost_details_for_a_leaf()
	{
		var port = CreatePortWithNodes();
		port.SeedWorker(new() {
			Sessions = [new(Session1, LeafId, new(At(9), At(11)))],
			EffectiveWorkingIntervals = [FullDay],
			ScheduledWorkingIntervals = [FullDay],
			Exceptions = [],
			NodeOverrides = [],
			UserCostRates = [],
			UserDefaultRate = new HourlyRate(60m),
		});
		var sut = new CostQueries(port);

		var result = await sut.GetCostDetailsAsync(new() { Context = ContextFor(CostViewerId), NodeId = LeafId, AsOf = At(24) });

		result.NodeId.Should().Be(LeafId);
		result.ExactCost.Should().Be(new Money(120m));
		result.DisplayedCost.Should().Be(new Money(120m));
		result.Trace.Should().OnlyContain(entry => entry.NodeId == LeafId);
		result.TzdbVersion.Should().Be(DateTimeZoneProviders.Tzdb.VersionId);
	}

	[Fact]
	public async Task A_worker_without_cost_viewing_permission_cannot_calculate_cost_details()
	{
		var port = CreatePortWithNodes();
		port.SeedWorker(new() {
			Sessions = [new(Session1, LeafId, new(At(9), At(11)))],
			EffectiveWorkingIntervals = [FullDay],
			ScheduledWorkingIntervals = [FullDay],
			Exceptions = [],
			NodeOverrides = [],
			UserCostRates = [],
			UserDefaultRate = new HourlyRate(60m),
		});
		var sut = new CostQueries(port);

		var act = () => sut.GetCostDetailsAsync(new() { Context = ContextFor(WorkerId), NodeId = LeafId, AsOf = At(24) });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
		port.GetActorRolesCallCount.Should().Be(1);
		port.GetCostInputsCallCount.Should().Be(0);
	}

	[Fact]
	public async Task Hierarchy_totals_reflect_a_workers_foreign_concurrent_session_without_exposing_it()
	{
		var port = CreatePortWithNodes();
		// One worker with two sessions overlapping [10:00,11:00): session1 on LeafId (inside
		// BranchId's subtree) and session2 on OtherLeafId (a sibling subtree). Correct N requires
		// discovering session2 database-wide (ADR 0017), but OtherLeafId must never appear in
		// BranchId's totals or trace.
		port.SeedWorker(new() {
			Sessions = [
				new(Session1, LeafId, new(At(9), At(11))),
				new(Session2, OtherLeafId, new(At(10), At(12))),
			],
			EffectiveWorkingIntervals = [FullDay],
			ScheduledWorkingIntervals = [FullDay],
			Exceptions = [],
			NodeOverrides = [],
			UserCostRates = [],
			UserDefaultRate = new HourlyRate(60m),
		});
		var sut = new CostQueries(port);

		var result = await sut.GetHierarchyTotalsAsync(new() { Context = ContextFor(CostViewerId), NodeId = BranchId, AsOf = At(24) });

		// [09:00,10:00) session1 alone: 1h @ 60 = 60. [10:00,11:00) both sessions share: 0.5h @ 60 = 30. Total 90.
		result.ExactCosts.Should().ContainKeys(BranchId, LeafId);
		result.ExactCosts.Should().NotContainKey(OtherLeafId);
		result.ExactCosts[LeafId].Should().Be(new Money(90m));
		result.ExactCosts[BranchId].Should().Be(new Money(90m));
		result.DisplayedCosts[BranchId].Should().Be(new Money(90m));
		result.DisplayedCosts[LeafId].Should().Be(new Money(90m));
		result.TzdbVersion.Should().Be(DateTimeZoneProviders.Tzdb.VersionId);
	}

	[Fact]
	public async Task A_worker_without_cost_viewing_permission_cannot_calculate_hierarchy_totals()
	{
		var sut = new CostQueries(CreatePortWithNodes());

		var act = () => sut.GetHierarchyTotalsAsync(new() { Context = ContextFor(WorkerId), NodeId = BranchId, AsOf = At(24) });

		await act.Should().ThrowAsync<AuthorizationDeniedException>();
	}

	[Fact]
	public async Task GetHierarchyTotalsAsync_rejects_a_subtree_larger_than_the_bounded_maximum()
	{
		// Remediation plan §3.1: cost/hierarchy is bounded by a hard node-count maximum rather than
		// allowing an arbitrarily large subtree to serialize unboundedly.
		const int NodeCountAboveMaximum = 50_001;
		var port = new FakeCostQueryPort();
		port.SeedRoles(CostViewerId, EmployeeRole.CostViewer);
		var childIds = Enumerable.Range(0, NodeCountAboveMaximum).Select(offset => new JobNodeId(RootId.Value + 1 + offset)).ToArray();
		port.SeedNode(new(RootId, null, [.. childIds], null));
		foreach (var childId in childIds) {
			port.SeedNode(new(childId, RootId, [], Achievement.InProgress));
		}

		var sut = new CostQueries(port);

		var act = () => sut.GetHierarchyTotalsAsync(new() { Context = ContextFor(CostViewerId), NodeId = RootId, AsOf = At(24) });

		await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
	}

	[Fact]
	public async Task GetCostDetailsAsync_rejects_a_trace_larger_than_the_requested_maximum()
	{
		var port = CreatePortWithNodes();
		port.SeedWorker(new() {
			Sessions = [
				new(Session1, LeafId, new(At(9), At(10))),
				new(Session2, LeafId, new(At(11), At(12))),
			],
			EffectiveWorkingIntervals = [FullDay],
			ScheduledWorkingIntervals = [FullDay],
			Exceptions = [],
			NodeOverrides = [],
			UserCostRates = [],
			UserDefaultRate = new HourlyRate(60m),
		});
		var sut = new CostQueries(port);

		var act = () => sut.GetCostDetailsAsync(new() {
			Context = ContextFor(CostViewerId),
			NodeId = LeafId,
			AsOf = At(24),
			MaxTraceSegments = 1,
		});

		await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
	}

	[Fact]
	public async Task GetHierarchyTotalsAsync_rejects_a_subtree_larger_than_the_requested_maximum()
	{
		var sut = new CostQueries(CreatePortWithNodes());

		var act = () => sut.GetHierarchyTotalsAsync(new() {
			Context = ContextFor(CostViewerId),
			NodeId = RootId,
			AsOf = At(24),
			MaxHierarchyNodes = 1,
		});

		await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void Constructor_rejects_a_null_port()
	{
		var act = () => new CostQueries(null!);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public async Task GetCostDetailsAsync_rejects_a_null_request()
	{
		var sut = new CostQueries(CreatePortWithNodes());

		Func<Task> act = () => sut.GetCostDetailsAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public void GetCostDetailsAsync_throws_synchronously_for_a_null_request()
	{
		var sut = new CostQueries(CreatePortWithNodes());

		Action act = () => _ = sut.GetCostDetailsAsync(null!);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public async Task GetHierarchyTotalsAsync_rejects_a_null_request()
	{
		var sut = new CostQueries(CreatePortWithNodes());

		var act = () => sut.GetHierarchyTotalsAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task GetBulkNodeCostsAsync_prices_every_candidate_from_one_snapshot_matching_individual_hierarchy_totals()
	{
		var port = CreatePortWithNodes();
		port.SeedWorker(new() {
			Sessions = [new(Session1, LeafId, new(At(9), At(11)))],
			EffectiveWorkingIntervals = [FullDay],
			ScheduledWorkingIntervals = [FullDay],
			Exceptions = [],
			NodeOverrides = [],
			UserCostRates = [],
			UserDefaultRate = new HourlyRate(60m),
		});
		var sut = new CostQueries(port);

		var bulk = await sut.GetBulkNodeCostsAsync(new() {
			Context = ContextFor(CostViewerId),
			NodeIds = [BranchId, LeafId, OtherLeafId],
			AsOf = At(24),
		});
		var individualBranch = await sut.GetHierarchyTotalsAsync(new() { Context = ContextFor(CostViewerId), NodeId = BranchId, AsOf = At(24) });
		var individualLeaf = await sut.GetHierarchyTotalsAsync(new() { Context = ContextFor(CostViewerId), NodeId = LeafId, AsOf = At(24) });

		bulk.DisplayedCosts[BranchId].Should().Be(individualBranch.DisplayedCosts[BranchId]);
		bulk.DisplayedCosts[LeafId].Should().Be(individualLeaf.DisplayedCosts[LeafId]);
		bulk.DisplayedCosts[OtherLeafId].Should().Be(new Money(0m));
	}

	[Fact]
	public async Task GetBulkNodeCostsAsync_makes_exactly_one_port_round_trip_regardless_of_candidate_count()
	{
		var port = CreatePortWithNodes();
		port.SeedWorker(new() {
			Sessions = [new(Session1, LeafId, new(At(9), At(11)))],
			EffectiveWorkingIntervals = [FullDay],
			ScheduledWorkingIntervals = [FullDay],
			Exceptions = [],
			NodeOverrides = [],
			UserCostRates = [],
			UserDefaultRate = new HourlyRate(60m),
		});
		var sut = new CostQueries(port);

		_ = await sut.GetBulkNodeCostsAsync(new() {
			Context = ContextFor(CostViewerId),
			NodeIds = [RootId, BranchId, LeafId, OtherLeafId],
			AsOf = At(24),
		});

		// Fresh-eyes review §2.8: one snapshot regardless of how many candidates were requested.
		port.GetActorRolesCallCount.Should().Be(0);
		port.GetBulkCostInputsCallCount.Should().Be(1);
	}

	[Fact]
	public async Task GetBulkNodeCostsAsync_admits_a_worker_who_owns_an_ancestor_without_cost_viewing_permission()
	{
		var port = CreatePortWithNodes();
		port.SeedOwner(BranchId, WorkerId);
		port.SeedWorker(new() {
			Sessions = [new(Session1, LeafId, new(At(9), At(11)))],
			EffectiveWorkingIntervals = [FullDay],
			ScheduledWorkingIntervals = [FullDay],
			Exceptions = [],
			NodeOverrides = [],
			UserCostRates = [],
			UserDefaultRate = new HourlyRate(60m),
		});
		var sut = new CostQueries(port);

		var bulk = await sut.GetBulkNodeCostsAsync(new() { Context = ContextFor(WorkerId), NodeIds = [LeafId], AsOf = At(24) });

		bulk.DisplayedCosts.Should().ContainKey(LeafId);
	}

	[Fact]
	public async Task GetBulkNodeCostsAsync_omits_a_candidate_the_actor_owns_neither_it_nor_an_ancestor_of()
	{
		var port = CreatePortWithNodes();
		port.SeedWorker(new() {
			Sessions = [new(Session1, LeafId, new(At(9), At(11)))],
			EffectiveWorkingIntervals = [FullDay],
			ScheduledWorkingIntervals = [FullDay],
			Exceptions = [],
			NodeOverrides = [],
			UserCostRates = [],
			UserDefaultRate = new HourlyRate(60m),
		});
		var sut = new CostQueries(port);

		var bulk = await sut.GetBulkNodeCostsAsync(new() { Context = ContextFor(WorkerId), NodeIds = [LeafId], AsOf = At(24) });

		bulk.DisplayedCosts.Should().NotContainKey(LeafId);
	}

	[Fact]
	public async Task GetBulkNodeCostsAsync_uses_snapshot_roles_when_the_actors_roles_change_during_the_operation()
	{
		var port = CreatePortWithNodes();
		port.SeedBulkSnapshotRoles(CostViewerId, EmployeeRole.Worker);
		var sut = new CostQueries(port);

		var bulk = await sut.GetBulkNodeCostsAsync(new() { Context = ContextFor(CostViewerId), NodeIds = [LeafId], AsOf = At(24) });

		bulk.DisplayedCosts.Should().NotContainKey(
			LeafId, "authorization must use the roles read in the same snapshot as the cost inputs");
	}

	[Fact]
	public async Task GetBulkNodeCostsAsync_returns_an_empty_result_without_a_port_round_trip_for_no_candidates()
	{
		var port = CreatePortWithNodes();
		var sut = new CostQueries(port);

		var bulk = await sut.GetBulkNodeCostsAsync(new() { Context = ContextFor(CostViewerId), NodeIds = [], AsOf = At(24) });

		bulk.DisplayedCosts.Should().BeEmpty();
		port.GetActorRolesCallCount.Should().Be(0);
		port.GetBulkCostInputsCallCount.Should().Be(0);
	}

	[Fact]
	public async Task GetBulkNodeCostsAsync_rejects_a_null_request()
	{
		var sut = new CostQueries(CreatePortWithNodes());

		var act = () => sut.GetBulkNodeCostsAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}
}
