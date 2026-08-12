namespace JobTrack.Domain.Costing;

using System.Runtime.InteropServices;
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
		// One override index and one priced-exception filter for the whole allocation set: the rate
		// resolution below runs once per allocation, and both regrouping the same unchanging overrides
		// and rescanning the same overwhelmingly-unpriced exception list per call dominated its cost.
		var overridesByNode = RateResolver.IndexOverridesByNode(nodeOverrides);
		var pricedExceptions = RateResolver.FilterPricedExceptions(exceptions);
		var leafCosts = new Dictionary<JobNodeId, decimal>();
		foreach (var allocation in allocations) {
			var rate = RateResolver.Resolve(
				allocation.NodeId, allocation.Segment.Start, nodesById, pricedExceptions, overridesByNode, userCostRates, userDefaultRate).Rate;
			// Single probe (Stage 2 item 2d): decimal's default (0m) is the correct additive
			// identity, so a not-yet-seen leaf needs no separate initialization branch.
			ref var cost = ref CollectionsMarshal.GetValueRefOrAddDefault(leafCosts, allocation.NodeId, out _);
			cost += SegmentCostCalculator.Calculate(allocation.Share, rate).Amount;
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

		// One pass and one in-place sort per segment, not GroupBy + a per-group OrderBy chain: this runs
		// against every allocation of every costed worker, and the enumerator/buffer churn was
		// measurable next to the arithmetic itself.
		var sessionIdsBySegment = new Dictionary<WorkInterval, List<WorkSessionId>>();
		foreach (var allocation in allocations) {
			ref var segmentSessionIds = ref CollectionsMarshal.GetValueRefOrAddDefault(sessionIdsBySegment, allocation.Segment, out var existed);
			if (!existed) {
				segmentSessionIds = [];
			}

			segmentSessionIds!.Add(allocation.SessionId);
		}

		var activeSessionsBySegment = new Dictionary<WorkInterval, EquatableArray<WorkSessionId>>(sessionIdsBySegment.Count);
		foreach (var (segment, segmentSessionIds) in sessionIdsBySegment) {
			segmentSessionIds.Sort(static (left, right) => left.Value.CompareTo(right.Value));
			activeSessionsBySegment[segment] = EquatableArray.CopyOf(segmentSessionIds);
		}

		// One override index and one priced-exception filter for every resolution below — see
		// ComputeLeafCosts.
		var overridesByNode = RateResolver.IndexOverridesByNode(nodeOverrides);
		var pricedExceptions = RateResolver.FilterPricedExceptions(exceptions);
		// One searchable index for every scheduled-working-time stamp below: the set never changes
		// across the trace, and its length grows with the costed window rather than with the answer.
		var scheduledIndex = IntervalIndex.Build(scheduledWorkingIntervals);
		var trace = new CostSegmentTrace[allocations.Count];
		var traceIndex = 0;
		foreach (var allocation in allocations) {
			var resolved = RateResolver.Resolve(
				allocation.NodeId, allocation.Segment.Start, nodesById, pricedExceptions, overridesByNode, userCostRates, userDefaultRate);
			trace[traceIndex++] = new(
				allocation.Segment,
				WorkingTimeEligibility.IsScheduledWorkingTime(allocation.Segment, scheduledIndex),
				activeSessionsBySegment[allocation.Segment],
				allocation.SessionId,
				allocation.NodeId,
				allocation.Share,
				resolved,
				SegmentCostCalculator.Calculate(allocation.Share, resolved.Rate));
		}

		// Array.Sort is unstable where the previous OrderBy/ThenBy was stable, so the comparison itself
		// carries a total order over every field a partitioner-produced entry can differ in.
		Array.Sort(trace, static (left, right) => {
			var bySegmentStart = left.Segment.Start.CompareTo(right.Segment.Start);
			if (bySegmentStart != 0) {
				return bySegmentStart;
			}

			var bySessionId = left.SessionId.Value.CompareTo(right.SessionId.Value);
			if (bySessionId != 0) {
				return bySessionId;
			}

			var bySegmentEnd = left.Segment.End.CompareTo(right.Segment.End);
			return bySegmentEnd != 0 ? bySegmentEnd : left.NodeId.Value.CompareTo(right.NodeId.Value);
		});
		var leafCostAmounts = new Dictionary<JobNodeId, decimal>();
		foreach (var entry in trace) {
			ref var amount = ref CollectionsMarshal.GetValueRefOrAddDefault(leafCostAmounts, entry.NodeId, out _);
			amount += entry.UnroundedContribution.Amount;
		}

		var leafCosts = leafCostAmounts.ToDictionary(entry => entry.Key, entry => new Money(entry.Value));
		var exactCosts = HierarchicalCostAggregator.Aggregate(nodeId, nodesById, leafCosts);
		var leafDurations = AllocatedDurationCalculator.ComputeLeafDurations(allocations);
		var allocatedDurations = HierarchicalAllocatedDurationAggregator.Aggregate(nodeId, nodesById, leafDurations);

		// The narrowed session list depends only on the segment, and trace entries sharing a segment
		// share one ActiveSessionIds instance — so narrow once per segment rather than once per entry.
		// Only segments that actually narrowed are recorded: for a calculation scoped to a root nothing
		// ever narrows, and when nothing narrowed anywhere every entry's own node is exposed too, so the
		// trace is reusable as-is instead of re-cloning every record.
		var narrowedSessionsBySegment = new Dictionary<WorkInterval, EquatableArray<WorkSessionId>>();
		foreach (var (segment, sessionIds) in activeSessionsBySegment) {
			if (TryNarrowToExposed(sessionIds, sessionNodeIds, exactCosts, out var narrowed)) {
				narrowedSessionsBySegment[segment] = narrowed;
			}
		}

		var exposedTrace = trace;
		if (narrowedSessionsBySegment.Count > 0) {
			var exposedEntries = new List<CostSegmentTrace>(trace.Length);
			foreach (var entry in trace) {
				if (!exactCosts.ContainsKey(entry.NodeId)) {
					continue;
				}

				exposedEntries.Add(narrowedSessionsBySegment.TryGetValue(entry.Segment, out var narrowed)
					? entry with {
						ActiveSessionIds = narrowed,
					}
					: entry);
			}

			exposedTrace = [.. exposedEntries];
		}

		return new(
			EquatableDictionaryFactory.CopyOf(exactCosts),
			EquatableDictionaryFactory.CopyOf(allocatedDurations),
			EquatableArray.CopyOf(exposedTrace));
	}

	/// <summary>
	///     Drops the session identifiers whose own node falls outside the costed subtree (ADR 0017).
	///     Returns <see langword="false" /> — leaving <paramref name="narrowed" /> as
	///     <paramref name="sessionIds" /> itself — when none do, the overwhelmingly common case for a
	///     calculation scoped to a root, where re-copying every segment's list would be pure waste.
	/// </summary>
	private static bool TryNarrowToExposed(
		EquatableArray<WorkSessionId> sessionIds,
		Dictionary<WorkSessionId, JobNodeId> sessionNodeIds,
		IReadOnlyDictionary<JobNodeId, Money> exactCosts,
		out EquatableArray<WorkSessionId> narrowed)
	{
		foreach (var sessionId in sessionIds) {
			if (!exactCosts.ContainsKey(sessionNodeIds[sessionId])) {
				narrowed = EquatableArray.CopyOf(sessionIds.Where(id => exactCosts.ContainsKey(sessionNodeIds[id])));
				return true;
			}
		}

		narrowed = sessionIds;
		return false;
	}
}
