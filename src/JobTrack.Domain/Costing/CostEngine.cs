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

	/// <summary>
	///     Computes each leaf's own exact cost contribution from <paramref name="allocations" />,
	///     independent of any particular subtree root — the same one worker's allocations are later
	///     aggregated under, potentially several times, by <see cref="HierarchicalCostAggregator.Aggregate" />
	///     for however many candidate roots need this worker's contribution (fresh-eyes review §2.8): the
	///     comparatively expensive per-segment rate resolution runs once per worker regardless of how many
	///     roots are being costed, while only the cheap tree-walk aggregation repeats per root.
	/// </summary>
	public static IReadOnlyDictionary<JobNodeId, Money> ComputeLeafCosts(
		IReadOnlyCollection<SessionSegmentAllocation> allocations,
		IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById,
		IReadOnlyCollection<ScheduleExceptionEntry> exceptions,
		IReadOnlyCollection<NodeRateOverride> nodeOverrides,
		IReadOnlyCollection<UserCostRate> userCostRates,
		HourlyRate? userDefaultRate)
	{
		// One override index for the whole allocation set: the rate resolution below runs once per
		// allocation, and regrouping the same unchanging overrides per call dominated its cost.
		var overridesByNode = RateResolver.IndexOverridesByNode(nodeOverrides);
		var leafCosts = new Dictionary<JobNodeId, decimal>();
		foreach (var allocation in allocations) {
			var rate = RateResolver.Resolve(
				allocation.NodeId, allocation.Segment.Start, nodesById, exceptions, overridesByNode, userCostRates, userDefaultRate).Rate;
			leafCosts[allocation.NodeId] =
				leafCosts.GetValueOrDefault(allocation.NodeId) + SegmentCostCalculator.Calculate(allocation.Share, rate).Amount;
		}

		return leafCosts.ToDictionary(entry => entry.Key, entry => new Money(entry.Value));
	}

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
		var sessionNodeIds = new Dictionary<WorkSessionId, JobNodeId>();
		foreach (var allocation in allocations) {
			_ = sessionNodeIds.TryAdd(allocation.SessionId, allocation.NodeId);
		}

		var activeSessionsBySegment = allocations
			.GroupBy(allocation => allocation.Segment)
			.ToDictionary(
				group => group.Key,
				group => EquatableArray.CopyOf(group.Select(allocation => allocation.SessionId).OrderBy(id => id.Value)));

		// One override index for every resolution below — see ComputeLeafCosts.
		var overridesByNode = RateResolver.IndexOverridesByNode(nodeOverrides);
		// One searchable index for every scheduled-working-time stamp below: the set never changes
		// across the trace, and its length grows with the costed window rather than with the answer.
		var scheduledIndex = IntervalIndex.Build(scheduledWorkingIntervals);
		var trace = allocations
			.Select(allocation => {
				var resolved = RateResolver.Resolve(
					allocation.NodeId, allocation.Segment.Start, nodesById, exceptions, overridesByNode, userCostRates, userDefaultRate);
				return new CostSegmentTrace(
					allocation.Segment,
					WorkingTimeEligibility.IsScheduledWorkingTime(allocation.Segment, scheduledIndex),
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
		var leafCostAmounts = new Dictionary<JobNodeId, decimal>();
		foreach (var entry in trace) {
			leafCostAmounts[entry.NodeId] = leafCostAmounts.GetValueOrDefault(entry.NodeId) + entry.UnroundedContribution.Amount;
		}

		var leafCosts = leafCostAmounts.ToDictionary(entry => entry.Key, entry => new Money(entry.Value));
		var exactCosts = HierarchicalCostAggregator.Aggregate(nodeId, nodesById, leafCosts);
		var leafDurations = AllocatedDurationCalculator.ComputeLeafDurations(allocations);
		var allocatedDurations = HierarchicalAllocatedDurationAggregator.Aggregate(nodeId, nodesById, leafDurations);

		// The narrowed session list depends only on the segment, and trace entries sharing a segment
		// share one ActiveSessionIds instance — so narrow once per segment rather than once per entry.
		var exposedSessionsBySegment = new Dictionary<WorkInterval, EquatableArray<WorkSessionId>>(activeSessionsBySegment.Count);
		foreach (var (segment, sessionIds) in activeSessionsBySegment) {
			exposedSessionsBySegment[segment] = NarrowToExposed(sessionIds, sessionNodeIds, exactCosts);
		}

		var exposedTrace = trace
			.Where(entry => exactCosts.ContainsKey(entry.NodeId))
			.Select(entry => entry with { ActiveSessionIds = exposedSessionsBySegment[entry.Segment] })
			.ToArray();

		return new(
			EquatableDictionaryFactory.CopyOf(exactCosts),
			EquatableDictionaryFactory.CopyOf(allocatedDurations),
			EquatableArray.CopyOf(exposedTrace));
	}

	/// <summary>
	///     Drops the session identifiers whose own node falls outside the costed subtree (ADR 0017),
	///     returning <paramref name="sessionIds" /> itself when none do — the overwhelmingly common case
	///     for a calculation scoped to a root, where re-copying every segment's list would be pure waste.
	/// </summary>
	private static EquatableArray<WorkSessionId> NarrowToExposed(
		EquatableArray<WorkSessionId> sessionIds,
		Dictionary<WorkSessionId, JobNodeId> sessionNodeIds,
		IReadOnlyDictionary<JobNodeId, Money> exactCosts)
	{
		foreach (var sessionId in sessionIds) {
			if (!exactCosts.ContainsKey(sessionNodeIds[sessionId])) {
				return EquatableArray.CopyOf(sessionIds.Where(id => exactCosts.ContainsKey(sessionNodeIds[id])));
			}
		}

		return sessionIds;
	}
}
