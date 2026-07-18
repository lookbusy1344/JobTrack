namespace JobTrack.Domain.Tests.Costing;

using Abstractions;
using AwesomeAssertions;
using Domain.Costing;
using Domain.Hierarchy;
using Domain.Intervals;
using Domain.Rates;
using Domain.Schedules;
using NodaTime;

public sealed class CostSegmentPartitionerTests
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
	public void The_spec_worked_example_partitions_into_three_segments_by_active_count()
	{
		var sessions = new[] {
			new CostableSession(Session1, LeafId, new(At(9), At(12))), new CostableSession(Session2, OtherLeafId, new(At(11), At(13))),
		};

		var allocations = CostSegmentPartitioner.Partition(
			sessions, [FullDay], TwoLeavesUnderRoot(), [], [], FullDay);

		var bySegment = allocations.GroupBy(a => a.Segment).OrderBy(g => g.Key.Start).ToList();
		bySegment.Should().HaveCount(3);

		var first = bySegment[0];
		first.Key.Should().Be(new WorkInterval(At(9), At(11)));
		first.Should().ContainSingle(a => a.SessionId == Session1);
		first.Single().Share.ConcurrencyDivisor.Should().Be(1);

		var second = bySegment[1];
		second.Key.Should().Be(new WorkInterval(At(11), At(12)));
		second.Select(a => a.SessionId).Should().BeEquivalentTo([Session1, Session2]);
		second.Should().OnlyContain(a => a.Share.ConcurrencyDivisor == 2);

		var third = bySegment[2];
		third.Key.Should().Be(new WorkInterval(At(12), At(13)));
		third.Should().ContainSingle(a => a.SessionId == Session2);
		third.Single().Share.ConcurrencyDivisor.Should().Be(1);
	}

	[Fact]
	public void Every_session_share_carries_the_exact_segment_tick_count()
	{
		var sessions = new[] { new CostableSession(Session1, LeafId, new(At(9), At(12))) };

		var allocations = CostSegmentPartitioner.Partition(
			sessions, [FullDay], SingleLeafUnderRoot(), [], [], FullDay);

		allocations.Should().ContainSingle();
		var allocation = allocations[0];
		allocation.Segment.Should().Be(new WorkInterval(At(9), At(12)));
		allocation.Share.SegmentTicks.Should().Be(allocation.Segment.Duration.BclCompatibleTicks);
		allocation.Share.ConcurrencyDivisor.Should().Be(1);
	}

	[Fact]
	public void Arbitrarily_many_concurrent_sessions_are_all_counted_in_N()
	{
		const int concurrentSessionCount = 25;
		var sessions = Enumerable.Range(0, concurrentSessionCount)
			.Select(i => new CostableSession(new(i), new(i + 2), new(At(9), At(10))))
			.ToArray();
		var leafIds = sessions.Select(session => session.NodeId).ToArray();
		var nodes = leafIds
			.Select(id => new HierarchyNode(id, RootId, [], Achievement.InProgress))
			.Append(new(RootId, null, [.. leafIds], null))
			.ToDictionary(node => node.Id);

		var allocations = CostSegmentPartitioner.Partition(
			sessions, [FullDay], nodes, [], [], FullDay);

		allocations.Should().HaveCount(concurrentSessionCount);
		allocations.Should().OnlyContain(a => a.Share.ConcurrencyDivisor == concurrentSessionCount);
		allocations.Select(a => a.SessionId).Distinct().Should().HaveCount(concurrentSessionCount);
	}

	[Fact]
	public void A_node_override_on_an_ancestor_introduces_a_boundary_even_though_it_is_not_the_active_session_edge()
	{
		var sessions = new[] { new CostableSession(Session1, LeafId, new(At(0), At(24))) };
		var rootOverride = new NodeRateOverride(RootId, new(50m), At(12), null);

		var allocations = CostSegmentPartitioner.Partition(
			sessions, [FullDay], SingleLeafUnderRoot(), [rootOverride], [], FullDay);

		var segments = allocations.Select(a => a.Segment).OrderBy(s => s.Start).ToList();
		segments.Should().BeEquivalentTo([new(At(0), At(12)), new WorkInterval(At(12), At(24))]);
		allocations.Should().OnlyContain(a => a.Share.ConcurrencyDivisor == 1);
	}

	[Fact]
	public void A_user_cost_rate_boundary_introduces_a_cut_independent_of_hierarchy()
	{
		var sessions = new[] { new CostableSession(Session1, LeafId, new(At(0), At(24))) };
		var userRate = new UserCostRate(new(30m), At(15), null);

		var allocations = CostSegmentPartitioner.Partition(
			sessions, [FullDay], SingleLeafUnderRoot(), [], [userRate], FullDay);

		var segments = allocations.Select(a => a.Segment).OrderBy(s => s.Start).ToList();
		segments.Should().BeEquivalentTo([new(At(0), At(15)), new WorkInterval(At(15), At(24))]);
	}

	[Fact]
	public void A_priced_additive_exception_inside_existing_working_time_introduces_rate_boundaries()
	{
		var sessions = new[] { new CostableSession(Session1, LeafId, new(At(9), At(12))) };
		var overtime = new ScheduleExceptionEntry(
			ScheduleExceptionEffect.AddWorkingTime, new(At(10), At(11)), new HourlyRate(100m));

		var allocations = CostSegmentPartitioner.Partition(
			sessions, [FullDay], SingleLeafUnderRoot(), [overtime], [], [], FullDay);

		allocations.Select(allocation => allocation.Segment).Should().Equal(new WorkInterval(At(9), At(10)), new WorkInterval(At(10), At(11)),
			new WorkInterval(At(11), At(12)));
	}

	[Fact]
	public void Overlapping_sessions_on_the_same_leaf_are_rejected_as_corrupt_input()
	{
		var sessions = new[] {
			new CostableSession(Session1, LeafId, new(At(9), At(12))), new CostableSession(Session2, LeafId, new(At(11), At(13))),
		};

		var act = () => CostSegmentPartitioner.Partition(
			sessions, [FullDay], SingleLeafUnderRoot(), [], [], [], FullDay);

		act.Should().Throw<InvariantViolationException>()
			.Where(exception => exception.ConstraintId == "work-session.same-user-leaf-overlap");
	}

	[Fact]
	public void A_session_entirely_outside_the_effective_working_set_produces_no_allocation()
	{
		var sessions = new[] { new CostableSession(Session1, LeafId, new(At(1), At(2))) };
		var workingHours = new WorkInterval(At(9), At(17));

		var allocations = CostSegmentPartitioner.Partition(
			sessions, [workingHours], SingleLeafUnderRoot(), [], [], FullDay);

		allocations.Should().BeEmpty();
	}

	[Fact]
	public void A_session_is_clipped_to_the_reporting_bounds()
	{
		var sessions = new[] { new CostableSession(Session1, LeafId, new(At(0), At(24))) };
		var bounds = new WorkInterval(At(9), At(17));

		var allocations = CostSegmentPartitioner.Partition(
			sessions, [FullDay], SingleLeafUnderRoot(), [], [], bounds);

		allocations.Should().ContainSingle();
		allocations[0].Segment.Should().Be(bounds);
	}
}
