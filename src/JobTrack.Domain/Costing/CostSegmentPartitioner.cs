namespace JobTrack.Domain.Costing;

using Abstractions;
using Hierarchy;
using Intervals;
using NodaTime;
using Rates;
using Schedules;

/// <summary>
///     Partitions a user's costable sessions into maximal segments of constant active-session
///     membership (spec §10.2/§10.3 steps 5-10) and computes each active session's exact <c>1/N</c>
///     share per segment. The boundary set is exhaustive per impl plan §6.5: every eligible session
///     edge, every user-cost-rate edge, and every node-rate-override edge declared on the session's
///     node or <em>any</em> of its ancestors — not only the ancestor whose override would actually win
///     under <see cref="RateResolver" />'s nearest-ancestor rule, because a farther override can still
///     change the resolved rate the instant a nearer one lapses. Schedule-exception edges are retained
///     separately because normalization can erase a priced additive exception inside an existing
///     working interval even though its rate still changes at both edges.
/// </summary>
public static class CostSegmentPartitioner
{
	/// <summary>
	///     Computes every <see cref="SessionSegmentAllocation" /> for <paramref name="sessions" /> within
	///     <paramref name="bounds" />.
	/// </summary>
	public static IReadOnlyList<SessionSegmentAllocation> Partition(
		IReadOnlyCollection<CostableSession> sessions,
		IReadOnlyCollection<WorkInterval> effectiveWorkingIntervals,
		IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById,
		IReadOnlyCollection<NodeRateOverride> nodeOverrides,
		IReadOnlyCollection<UserCostRate> userCostRates,
		WorkInterval bounds) =>
		Partition(sessions, effectiveWorkingIntervals, nodesById, [], nodeOverrides, userCostRates, bounds);

	/// <summary>
	///     Computes allocations for sessions on <paramref name="includedNodeIds" /> only, while still
	///     counting every active session in each concurrency divisor, and throws before materializing
	///     more than <paramref name="maximumAllocationCount" /> allocations.
	/// </summary>
	public static IReadOnlyList<SessionSegmentAllocation> PartitionBounded(
		IReadOnlyCollection<CostableSession> sessions,
		IReadOnlyCollection<WorkInterval> effectiveWorkingIntervals,
		IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById,
		IReadOnlyCollection<ScheduleExceptionEntry> exceptions,
		IReadOnlyCollection<NodeRateOverride> nodeOverrides,
		IReadOnlyCollection<UserCostRate> userCostRates,
		WorkInterval bounds,
		IReadOnlySet<JobNodeId> includedNodeIds,
		int maximumAllocationCount)
	{
		ArgumentNullException.ThrowIfNull(includedNodeIds);
		ArgumentOutOfRangeException.ThrowIfNegative(maximumAllocationCount);

		return PartitionCore(
			sessions, effectiveWorkingIntervals, nodesById, exceptions, nodeOverrides, userCostRates,
			bounds, includedNodeIds, maximumAllocationCount);
	}

	/// <summary>
	///     Computes allocations while retaining schedule-exception edges that working-set normalization
	///     may otherwise erase, particularly priced additive exceptions inside normal working time.
	/// </summary>
	public static IReadOnlyList<SessionSegmentAllocation> Partition(
		IReadOnlyCollection<CostableSession> sessions,
		IReadOnlyCollection<WorkInterval> effectiveWorkingIntervals,
		IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById,
		IReadOnlyCollection<ScheduleExceptionEntry> exceptions,
		IReadOnlyCollection<NodeRateOverride> nodeOverrides,
		IReadOnlyCollection<UserCostRate> userCostRates,
		WorkInterval bounds) =>
		PartitionCore(
			sessions, effectiveWorkingIntervals, nodesById, exceptions, nodeOverrides, userCostRates,
			bounds, null, int.MaxValue);

	private static List<SessionSegmentAllocation> PartitionCore(
		IReadOnlyCollection<CostableSession> sessions,
		IReadOnlyCollection<WorkInterval> effectiveWorkingIntervals,
		IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById,
		IReadOnlyCollection<ScheduleExceptionEntry> exceptions,
		IReadOnlyCollection<NodeRateOverride> nodeOverrides,
		IReadOnlyCollection<UserCostRate> userCostRates,
		WorkInterval bounds,
		IReadOnlySet<JobNodeId>? includedNodeIds,
		int maximumAllocationCount)
	{
		ValidateNoSameLeafOverlap(sessions);
		var eligiblePieces = EligiblePieces(sessions, effectiveWorkingIntervals, bounds);
		if (eligiblePieces.Count == 0) {
			return [];
		}

		var boundaries = Boundaries(eligiblePieces, nodesById, exceptions, nodeOverrides, userCostRates, bounds);
		var startingAt = new Dictionary<Instant, List<int>>();
		var endingAt = new Dictionary<Instant, List<int>>();
		for (var index = 0; index < eligiblePieces.Count; ++index) {
			var interval = eligiblePieces[index].Interval;
			IndexAt(startingAt, interval.Start, index);
			IndexAt(endingAt, interval.End, index);
		}

		var activeIndexes = new SortedSet<int>();

		var allocations = new List<SessionSegmentAllocation>();
		for (var i = 0; i < boundaries.Count - 1; ++i) {
			if (endingAt.TryGetValue(boundaries[i], out var endingIndexes)) {
				foreach (var index in endingIndexes) {
					_ = activeIndexes.Remove(index);
				}
			}

			if (startingAt.TryGetValue(boundaries[i], out var startingIndexes)) {
				foreach (var index in startingIndexes) {
					_ = activeIndexes.Add(index);
				}
			}

			var segment = new WorkInterval(boundaries[i], boundaries[i + 1]);
			if (activeIndexes.Count == 0) {
				continue;
			}

			var share = new AllocatedShare(segment.Duration.BclCompatibleTicks, activeIndexes.Count);
			foreach (var index in activeIndexes) {
				var piece = eligiblePieces[index];
				if (includedNodeIds is not null && !includedNodeIds.Contains(piece.Session.NodeId)) {
					continue;
				}

				if (allocations.Count >= maximumAllocationCount) {
					throw new ArgumentOutOfRangeException(
						nameof(maximumAllocationCount),
						maximumAllocationCount,
						$"The cost allocation count exceeds the {maximumAllocationCount}-allocation maximum.");
				}

				allocations.Add(new(segment, piece.Session.SessionId, piece.Session.NodeId, share));
			}
		}

		return allocations;
	}

	private static void IndexAt(Dictionary<Instant, List<int>> index, Instant at, int pieceIndex)
	{
		if (!index.TryGetValue(at, out var indexes)) {
			indexes = [];
			index[at] = indexes;
		}

		indexes.Add(pieceIndex);
	}

	/// <summary>
	///     Intersects every session with the working set. Queries a <see cref="IntervalIndex" /> built
	///     once instead of scanning every working interval per session: the working set holds one
	///     interval per working day across the costed window, so the previous nested scan grew with the
	///     window's length even though the eligible pieces it found did not.
	/// </summary>
	private static List<(CostableSession Session, WorkInterval Interval)> EligiblePieces(
		IReadOnlyCollection<CostableSession> sessions, IReadOnlyCollection<WorkInterval> effectiveWorkingIntervals, WorkInterval bounds)
	{
		var workingIndex = IntervalIndex.Build(effectiveWorkingIntervals);
		var pieces = new List<(CostableSession Session, WorkInterval Interval)>();
		foreach (var session in sessions) {
			var clippedToBounds = IntervalAlgebra.Intersect(session.Interval, bounds);
			if (clippedToBounds is not WorkInterval clipped) {
				continue;
			}

			foreach (var workingInterval in workingIndex.Overlapping(clipped)) {
				if (IntervalAlgebra.Intersect(clipped, workingInterval) is WorkInterval piece) {
					pieces.Add((session, piece));
				}
			}
		}

		return pieces;
	}

	private static List<Instant> Boundaries(
		IReadOnlyCollection<(CostableSession Session, WorkInterval Interval)> eligiblePieces,
		IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById,
		IReadOnlyCollection<ScheduleExceptionEntry> exceptions,
		IReadOnlyCollection<NodeRateOverride> nodeOverrides,
		IReadOnlyCollection<UserCostRate> userCostRates,
		WorkInterval bounds)
	{
		var overridesByNode = RateResolver.IndexOverridesByNode(nodeOverrides);

		var boundaries = new SortedSet<Instant> { bounds.Start, bounds.End };
		// Every piece on the same leaf walks the same ancestor chain and contributes the same override
		// edges; walking each node once is enough, since `boundaries` is a set either way.
		var walkedNodes = new HashSet<JobNodeId>();
		foreach (var (session, interval) in eligiblePieces) {
			_ = boundaries.Add(interval.Start);
			_ = boundaries.Add(interval.End);

			JobNodeId? ancestorId = session.NodeId;
			while (ancestorId is JobNodeId id && walkedNodes.Add(id)) {
				if (overridesByNode.TryGetValue(id, out var overrides)) {
					foreach (var nodeOverride in overrides) {
						AddClippedBoundary(boundaries, nodeOverride.EffectiveStart, nodeOverride.EffectiveEnd, bounds);
					}
				}

				ancestorId = HierarchyNodeLookup.GetRequired(nodesById, id).ParentId;
			}
		}

		foreach (var rate in userCostRates) {
			AddClippedBoundary(boundaries, rate.EffectiveStart, rate.EffectiveEnd, bounds);
		}

		foreach (var exception in exceptions) {
			AddClippedBoundary(boundaries, exception.Interval.Start, exception.Interval.End, bounds);
		}

		return [.. boundaries];
	}

	private static void AddClippedBoundary(SortedSet<Instant> boundaries, Instant start, Instant? end, WorkInterval bounds)
	{
		if (start > bounds.Start && start < bounds.End) {
			_ = boundaries.Add(start);
		}

		if (end is Instant exclusiveEnd && exclusiveEnd > bounds.Start && exclusiveEnd < bounds.End) {
			_ = boundaries.Add(exclusiveEnd);
		}
	}

	private static void ValidateNoSameLeafOverlap(IReadOnlyCollection<CostableSession> sessions)
	{
		foreach (var group in sessions.GroupBy(session => session.NodeId)) {
			CostableSession? previous = null;
			foreach (var session in group.OrderBy(session => session.Interval.Start).ThenBy(session => session.Interval.End)) {
				if (previous is not null && IntervalAlgebra.Overlaps(previous.Interval, session.Interval)) {
					throw new InvariantViolationException(
						"work-session.same-user-leaf-overlap",
						$"Sessions {previous.SessionId.Value} and {session.SessionId.Value} overlap on leaf {session.NodeId.Value}.");
				}

				previous = session;
			}
		}
	}
}
