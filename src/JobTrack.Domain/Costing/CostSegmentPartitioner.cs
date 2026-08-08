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

		// Two flat piece-index arrays, one sorted by interval start and one by interval end, swept with
		// cursors as the boundary walk advances -- every piece edge is itself a boundary, so cursor
		// catch-up at each boundary visits each piece exactly once overall. Flat sorted arrays rather
		// than per-instant dictionaries of lists: the piece count grows with the costed window, and this
		// loop's allocation churn and pointer chasing were measurable next to the arithmetic itself.
		var startOrder = new int[eligiblePieces.Count];
		var endOrder = new int[eligiblePieces.Count];
		var startKeys = new Instant[eligiblePieces.Count];
		var endKeys = new Instant[eligiblePieces.Count];
		for (var index = 0; index < eligiblePieces.Count; ++index) {
			startOrder[index] = index;
			endOrder[index] = index;
			startKeys[index] = eligiblePieces[index].Interval.Start;
			endKeys[index] = eligiblePieces[index].Interval.End;
		}

		Array.Sort(startKeys, startOrder);
		Array.Sort(endKeys, endOrder);

		var startCursor = 0;
		var endCursor = 0;

		// Packed-array + sparse-slot active set, not a SortedSet<int>: the SortedSet is a red-black
		// tree (a heap node per insert, pointer-chased enumeration) enumerated once per boundary, and
		// this file already replaced its other tree/dictionary structures with flat arrays for
		// exactly this reason. `active` holds the currently-active piece indices densely in
		// `active[0..activeCount)`; `slotOf[pieceIndex]` is that piece's position in `active`, so both
		// add (append) and remove (swap the removed slot with the last active entry) are O(1) with no
		// allocation. This changes active-index iteration order relative to the old ascending
		// SortedSet order, which is safe here: CostEngine.Calculate re-sorts its trace under a total
		// order and sorts each segment's session list, and the property-test oracle for this method
		// (CostSegmentPartitionerPropertyTests) already canonicalizes (sorts) allocations before
		// comparing, precisely because Partition never promised emission order.
		var active = new int[eligiblePieces.Count];
		var slotOf = new int[eligiblePieces.Count];
		var activeCount = 0;

		var allocations = new List<SessionSegmentAllocation>();
		for (var i = 0; i < boundaries.Count - 1; ++i) {
			while (endCursor < endKeys.Length && endKeys[endCursor] <= boundaries[i]) {
				var removedPiece = endOrder[endCursor];
				var slot = slotOf[removedPiece];
				var lastSlot = --activeCount;
				var movedPiece = active[lastSlot];
				active[slot] = movedPiece;
				slotOf[movedPiece] = slot;
				++endCursor;
			}

			while (startCursor < startKeys.Length && startKeys[startCursor] <= boundaries[i]) {
				var addedPiece = startOrder[startCursor];
				active[activeCount] = addedPiece;
				slotOf[addedPiece] = activeCount;
				++activeCount;
				++startCursor;
			}

			var segment = new WorkInterval(boundaries[i], boundaries[i + 1]);
			if (activeCount == 0) {
				continue;
			}

			var share = new AllocatedShare(segment.Duration.BclCompatibleTicks, activeCount);
			for (var activeSlot = 0; activeSlot < activeCount; ++activeSlot) {
				var piece = eligiblePieces[active[activeSlot]];
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

		// A flat list sorted and deduplicated once at the end, not a SortedSet: the boundary count grows
		// with the costed window, and per-instant tree nodes were measurable allocation churn next to
		// the sweep itself.
		var boundaries = new List<Instant> { bounds.Start, bounds.End };
		// Every piece on the same leaf walks the same ancestor chain and contributes the same override
		// edges; walking each node once is enough, since `boundaries` is deduplicated either way.
		var walkedNodes = new HashSet<JobNodeId>();
		foreach (var (session, interval) in eligiblePieces) {
			boundaries.Add(interval.Start);
			boundaries.Add(interval.End);

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

		boundaries.Sort();
		var lastUnique = 0;
		for (var index = 1; index < boundaries.Count; ++index) {
			if (boundaries[index] != boundaries[lastUnique]) {
				boundaries[++lastUnique] = boundaries[index];
			}
		}

		boundaries.RemoveRange(lastUnique + 1, boundaries.Count - lastUnique - 1);
		return boundaries;
	}

	private static void AddClippedBoundary(List<Instant> boundaries, Instant start, Instant? end, WorkInterval bounds)
	{
		if (start > bounds.Start && start < bounds.End) {
			boundaries.Add(start);
		}

		if (end is Instant exclusiveEnd && exclusiveEnd > bounds.Start && exclusiveEnd < bounds.End) {
			boundaries.Add(exclusiveEnd);
		}
	}

	private static void ValidateNoSameLeafOverlap(IReadOnlyCollection<CostableSession> sessions)
	{
		// One sort by (leaf, start, end) puts each leaf's sessions adjacent, so overlap needs only each
		// consecutive same-leaf pair -- no per-leaf grouping structures for a check that runs against
		// every costed session set.
		var ordered = sessions.ToArray();
		Array.Sort(ordered, static (left, right) => {
			var byNode = left.NodeId.Value.CompareTo(right.NodeId.Value);
			if (byNode != 0) {
				return byNode;
			}

			var byStart = left.Interval.Start.CompareTo(right.Interval.Start);
			return byStart != 0 ? byStart : left.Interval.End.CompareTo(right.Interval.End);
		});
		for (var index = 1; index < ordered.Length; ++index) {
			var previous = ordered[index - 1];
			var session = ordered[index];
			if (previous.NodeId == session.NodeId && IntervalAlgebra.Overlaps(previous.Interval, session.Interval)) {
				throw new InvariantViolationException(
					"work-session.same-user-leaf-overlap",
					$"Sessions {previous.SessionId.Value} and {session.SessionId.Value} overlap on leaf {session.NodeId.Value}.");
			}
		}
	}
}
