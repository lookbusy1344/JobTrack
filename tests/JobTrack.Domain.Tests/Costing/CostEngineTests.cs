namespace JobTrack.Domain.Tests.Costing;

using Abstractions;
using AwesomeAssertions;
using Domain.Costing;
using Domain.Hierarchy;
using Domain.Intervals;
using Domain.Rates;
using Domain.Schedules;
using NodaTime;

public sealed class CostEngineTests
{
	private static readonly JobNodeId RootId = new(1);
	private static readonly JobNodeId LeafId = new(2);
	private static readonly JobNodeId OtherLeafId = new(3);
	private static readonly WorkSessionId Session1 = new(1);
	private static readonly WorkSessionId Session2 = new(2);

	private static readonly WorkInterval FullDay = new(At(0), At(24));

	private static Instant At(int hour) => hour == 24 ? Instant.FromUtc(2024, 1, 2, 0, 0) : Instant.FromUtc(2024, 1, 1, hour, 0);

	private static Dictionary<JobNodeId, HierarchyNode> SingleLeafUnderRoot()
	{
		var root = new HierarchyNode(RootId, null, [LeafId], null);
		var leaf = new HierarchyNode(LeafId, RootId, [], Achievement.InProgress);
		return new() { [RootId] = root, [LeafId] = leaf };
	}

	private static Dictionary<JobNodeId, HierarchyNode> TwoLeavesUnderRoot()
	{
		var root = new HierarchyNode(RootId, null, [LeafId, OtherLeafId], null);
		var first = new HierarchyNode(LeafId, RootId, [], Achievement.InProgress);
		var second = new HierarchyNode(OtherLeafId, RootId, [], Achievement.InProgress);
		return new() { [RootId] = root, [LeafId] = first, [OtherLeafId] = second };
	}

	[Fact]
	public void A_single_uncontested_session_costs_its_full_duration_at_the_default_rate()
	{
		var sessions = new[] { new CostableSession(Session1, LeafId, new(At(9), At(11))) };
		var allocations = CostSegmentPartitioner.Partition(sessions, [FullDay], SingleLeafUnderRoot(), [], [], FullDay);

		var costs = CostEngine.AggregateExactCosts(RootId, allocations, SingleLeafUnderRoot(), [FullDay], [], [], [], new HourlyRate(60m));

		costs[LeafId].Should().Be(new Money(120m));
		costs[RootId].Should().Be(new Money(120m));
	}

	[Fact]
	public void The_spec_worked_example_produces_the_hand_calculated_leaf_and_root_cost()
	{
		// [09:00,11:00) session1 alone: 2h @ 60 = 120.
		// [11:00,12:00) both sessions share 1h: each 0.5h @ 60 = 30 + 30.
		// [12:00,13:00) session2 alone: 1h @ 60 = 60.
		// Total: 120 + 30 + 30 + 60 = 240.
		var sessions = new[] {
			new CostableSession(Session1, LeafId, new(At(9), At(12))), new CostableSession(Session2, OtherLeafId, new(At(11), At(13))),
		};
		var nodes = TwoLeavesUnderRoot();
		var allocations = CostSegmentPartitioner.Partition(sessions, [FullDay], nodes, [], [], FullDay);

		var costs = CostEngine.AggregateExactCosts(RootId, allocations, nodes, [FullDay], [], [], [], new HourlyRate(60m));

		costs[LeafId].Should().Be(new Money(150m));
		costs[OtherLeafId].Should().Be(new Money(90m));
		costs[RootId].Should().Be(new Money(240m));
	}

	[Fact]
	public void Concurrent_sessions_produce_a_deterministically_ordered_trace_and_active_session_ids()
	{
		// Same overlap as the spec worked example: [11:00,12:00) has both sessions active in one
		// segment. The trace is a canonical explanation surfaced to callers (audit/display), so its
		// entry order (by segment start, then session id) and each segment's ActiveSessionIds order
		// (ascending session id) must be deterministic rather than an artifact of allocation order.
		var sessions = new[] {
			new CostableSession(Session2, OtherLeafId, new(At(11), At(13))), new CostableSession(Session1, LeafId, new(At(9), At(12))),
		};
		var nodes = TwoLeavesUnderRoot();
		var allocations = CostSegmentPartitioner.Partition(sessions, [FullDay], nodes, [], [], FullDay);

		var result = CostEngine.Calculate(RootId, allocations, nodes, [FullDay], [], [], [], new HourlyRate(60m));

		result.Trace.Select(entry => entry.Segment.Start).Should().BeInAscendingOrder();
		var overlapSegmentIds = result.Trace
			.Where(entry => entry.Segment == new WorkInterval(At(11), At(12)))
			.Select(entry => entry.SessionId)
			.ToArray();
		overlapSegmentIds.Should().Equal(Session1, Session2);

		var overlapActiveSessionIds = result.Trace
			.Single(entry => entry.Segment == new WorkInterval(At(11), At(12)) && entry.SessionId == Session1)
			.ActiveSessionIds;
		overlapActiveSessionIds.Should().Equal(Session1, Session2);
	}

	[Fact]
	public void A_node_override_boundary_changes_the_rate_applied_to_each_side_of_the_split()
	{
		var sessions = new[] { new CostableSession(Session1, LeafId, new(At(0), At(24))) };
		var rootOverride = new NodeRateOverride(RootId, new(100m), At(12), null);
		var allocations = CostSegmentPartitioner.Partition(sessions, [FullDay], SingleLeafUnderRoot(), [rootOverride], [], FullDay);

		var costs = CostEngine.AggregateExactCosts(RootId, allocations, SingleLeafUnderRoot(), [FullDay], [], [rootOverride], [],
			new HourlyRate(60m));

		// [00:00,12:00): no override yet, default rate 60 -> 12h * 60 = 720.
		// [12:00,24:00): root override applies, rate 100 -> 12h * 100 = 1200.
		costs[LeafId].Should().Be(new Money(1920m));
	}

	[Fact]
	public void Calculation_returns_the_canonical_explainable_segment_trace()
	{
		var sessions = new[] { new CostableSession(Session1, LeafId, new(At(9), At(11))) };
		var nodes = SingleLeafUnderRoot();
		var allocations = CostSegmentPartitioner.Partition(sessions, [FullDay], nodes, [], [], FullDay);

		var result = CostEngine.Calculate(RootId, allocations, nodes, [FullDay], [], [], [], new HourlyRate(60m));

		result.ExactCosts[RootId].Should().Be(new Money(120m));
		result.Trace.Should().ContainSingle();
		result.Trace[0].Should().Be(new CostSegmentTrace(
			new(At(9), At(11)),
			true,
			[Session1],
			Session1,
			LeafId,
			new(Duration.FromHours(2).BclCompatibleTicks, 1),
			new(new(60m), RateSource.UserDefault),
			new(120m)));
	}

	[Fact]
	public void A_foreign_sessions_concurrency_contribution_influences_cost_without_leaking_its_identity()
	{
		// Root has two sibling branches: BranchA (containing LeafId) and OtherLeafId (a leaf
		// directly under root). Both sessions belong to one worker and overlap [10:00,11:00),
		// so they share the concurrency divisor even though OtherLeafId sits outside BranchA's
		// subtree — the scenario ADR 0017 exists for.
		var branchId = new JobNodeId(4);
		var root = new HierarchyNode(RootId, null, [branchId, OtherLeafId], null);
		var branch = new HierarchyNode(branchId, RootId, [LeafId], null);
		var leaf = new HierarchyNode(LeafId, branchId, [], Achievement.InProgress);
		var otherLeaf = new HierarchyNode(OtherLeafId, RootId, [], Achievement.InProgress);
		var nodes = new Dictionary<JobNodeId, HierarchyNode> {
			[RootId] = root,
			[branchId] = branch,
			[LeafId] = leaf,
			[OtherLeafId] = otherLeaf,
		};
		var sessions = new[] {
			new CostableSession(Session1, LeafId, new(At(9), At(11))), new CostableSession(Session2, OtherLeafId, new(At(10), At(12))),
		};
		var allocations = CostSegmentPartitioner.Partition(sessions, [FullDay], nodes, [], [], FullDay);

		var result = CostEngine.Calculate(branchId, allocations, nodes, [FullDay], [], [], [], new HourlyRate(60m));

		// [09:00,10:00) session1 alone: 1h @ 60 = 60.
		// [10:00,11:00) both sessions share: session1 gets 0.5h @ 60 = 30.
		result.ExactCosts.Should().ContainKeys(branchId, LeafId);
		result.ExactCosts.Should().NotContainKey(OtherLeafId);
		result.ExactCosts[branchId].Should().Be(new Money(90m));

		result.Trace.Should().OnlyContain(entry => entry.NodeId == LeafId);
		result.Trace.SelectMany(entry => entry.ActiveSessionIds).Should().NotContain(Session2);
	}

	[Fact]
	public void A_priced_additive_exception_inside_normal_working_time_is_costed_at_its_override_rate()
	{
		var sessions = new[] { new CostableSession(Session1, LeafId, new(At(9), At(12))) };
		var overtime = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.AddWorkingTime, new(At(10), At(11)), new HourlyRate(100m));
		var nodes = SingleLeafUnderRoot();
		var allocations = CostSegmentPartitioner.Partition(sessions, [FullDay], nodes, [overtime], [], [], FullDay);

		var result = CostEngine.Calculate(RootId, allocations, nodes, [FullDay], [overtime], [], [], new HourlyRate(60m));

		result.ExactCosts[RootId].Should().Be(new Money(220m));
		result.Trace.Select(entry => entry.ResolvedRate.Rate).Should().Equal(new HourlyRate(60m), new HourlyRate(100m), new HourlyRate(60m));
	}

	[Fact]
	public void Trace_marks_exception_only_time_as_not_scheduled_working_time()
	{
		var scheduled = new[] { new WorkInterval(At(9), At(17)) };
		var overtime = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.AddWorkingTime, new(At(18), At(20)), null);
		var effective = ScheduleExceptionResolver.Apply(scheduled, [overtime]);
		var sessions = new[] { new CostableSession(Session1, LeafId, new(At(18), At(19))) };
		var nodes = SingleLeafUnderRoot();
		var allocations = CostSegmentPartitioner.Partition(sessions, effective, nodes, [overtime], [], [], FullDay);

		var result = CostEngine.Calculate(RootId, allocations, nodes, scheduled, [overtime], [], [], new HourlyRate(60m));

		result.Trace.Should().ContainSingle().Which.IsWorkingTime.Should().BeFalse();
	}
}
