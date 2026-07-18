namespace JobTrack.Domain.Costing;

using Abstractions;
using Hierarchy;
using Intervals;
using Rates;
using Schedules;

/// <summary>
///     The pure cost engine's final aggregation stage (spec §7.4/§10.3 steps 11-12, §10.4): resolves
///     each <see cref="CostSegmentPartitioner" /> output's applicable rate independently, computes its
///     exact monetary contribution, sums contributions per leaf node, and aggregates through the
///     hierarchy. Deterministic and side-effect-free over already-materialized inputs — no I/O, no
///     authorization filtering; those are the persistence layer's job (ADR 0017). The input allocations
///     may include sessions outside the requested node's subtree — a worker's database-wide overlapping
///     sessions are required to compute a correct concurrency divisor — but
///     <see
///         cref="CostCalculation.Trace" />
///     exposes only the nodes <see cref="CostCalculation.ExactCosts" />
///     reports on, with every entry's <see cref="CostSegmentTrace.ActiveSessionIds" /> narrowed the same
///     way, so a caller scoped to the requested node never receives a foreign session's identifier,
///     node, or rate (ADR 0017).
/// </summary>
public static class CostEngine
{
	/// <summary>
	///     Computes the exact actual cost of <paramref name="nodeId" /> and every node in its subtree
	///     from <paramref name="allocations" /> (the output of <see cref="CostSegmentPartitioner" />).
	/// </summary>
	public static IReadOnlyDictionary<JobNodeId, Money> AggregateExactCosts(
		JobNodeId nodeId,
		IReadOnlyCollection<SessionSegmentAllocation> allocations,
		IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById,
		IReadOnlyCollection<WorkInterval> scheduledWorkingIntervals,
		IReadOnlyCollection<ScheduleExceptionEntry> exceptions,
		IReadOnlyCollection<NodeRateOverride> nodeOverrides,
		IReadOnlyCollection<UserCostRate> userCostRates,
		HourlyRate? userDefaultRate)
		=> Calculate(
			nodeId, allocations, nodesById, scheduledWorkingIntervals, exceptions, nodeOverrides, userCostRates, userDefaultRate).ExactCosts;

	/// <summary>Computes exact hierarchy costs together with their canonical segment trace.</summary>
	public static CostCalculation Calculate(
		JobNodeId nodeId,
		IReadOnlyCollection<SessionSegmentAllocation> allocations,
		IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById,
		IReadOnlyCollection<WorkInterval> scheduledWorkingIntervals,
		IReadOnlyCollection<ScheduleExceptionEntry> exceptions,
		IReadOnlyCollection<NodeRateOverride> nodeOverrides,
		IReadOnlyCollection<UserCostRate> userCostRates,
		HourlyRate? userDefaultRate)
	{
		var sessionNodeIds = allocations
			.GroupBy(allocation => allocation.SessionId)
			.ToDictionary(group => group.Key, group => group.First().NodeId);
		var activeSessionsBySegment = allocations
			.GroupBy(allocation => allocation.Segment)
			.ToDictionary(
				group => group.Key,
				group => EquatableArray.CopyOf(group.Select(allocation => allocation.SessionId).OrderBy(id => id.Value)));
		var trace = allocations
			.Select(allocation => {
				var resolved = RateResolver.Resolve(
					allocation.NodeId, allocation.Segment.Start, nodesById, exceptions, nodeOverrides, userCostRates, userDefaultRate);
				return new CostSegmentTrace(
					allocation.Segment,
					WorkingTimeEligibility.IsScheduledWorkingTime(allocation.Segment, scheduledWorkingIntervals),
					activeSessionsBySegment[allocation.Segment],
					allocation.SessionId,
					allocation.NodeId,
					allocation.Share,
					resolved,
					SegmentCostCalculator.Calculate(allocation.Share, resolved.Rate));
			})
			.OrderBy(entry => entry.Segment.Start)
			.ThenBy(entry => entry.SessionId.Value)
			.ToArray();
		var leafCosts = trace
			.GroupBy(entry => entry.NodeId)
			.ToDictionary(group => group.Key, group => new Money(group.Sum(entry => entry.UnroundedContribution.Amount)));
		var exactCosts = HierarchicalCostAggregator.Aggregate(nodeId, nodesById, leafCosts);

		var exposedTrace = trace
			.Where(entry => exactCosts.ContainsKey(entry.NodeId))
			.Select(entry => entry with {
				ActiveSessionIds = EquatableArray.CopyOf(
					entry.ActiveSessionIds.Where(sessionId => exactCosts.ContainsKey(sessionNodeIds[sessionId]))),
			})
			.ToArray();

		return new(EquatableDictionaryFactory.CopyOf(exactCosts), EquatableArray.CopyOf(exposedTrace));
	}
}
