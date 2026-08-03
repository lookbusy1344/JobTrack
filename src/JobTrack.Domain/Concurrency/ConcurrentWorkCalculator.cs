namespace JobTrack.Domain.Concurrency;

using Abstractions;
using Intervals;
using NodaTime;

/// <summary>
///     Pure aggregation of which other jobs a worker was clocked on to at the same time as the subject
///     job (spec §4.4: one worker's sessions on <em>different</em> leaves may overlap deliberately, and
///     §10.2 makes that overlap the concurrency divisor the cost engine allocates by). Intersects each
///     subject session against every other session of the <em>same</em> worker on a different node and
///     sums the result per (worker, other node).
///     <para>
///         Wall-clock only: no schedule, working-time eligibility, or rate enters here, so a row reports
///         what was recorded rather than what was costed. Double counting is not possible on the subject
///         side because one worker's sessions on a single leaf can never overlap each other (the
///         <c>work_session_no_same_leaf_user_overlap</c> exclusion constraint), so summing per subject
///         session sums disjoint time.
///     </para>
/// </summary>
public static class ConcurrentWorkCalculator
{
	/// <summary>
	///     Computes every <see cref="ConcurrentWorkOverlap" /> between <paramref name="subjectSessions" />
	///     and <paramref name="concurrentSessions" />, ordered so each worker's rows are contiguous:
	///     workers by descending total concurrent time (then by id), and within a worker by descending
	///     overlap (then by node id), so the ordering is total and stable regardless of input order.
	/// </summary>
	/// <param name="subjectSessions">The sessions recorded against the job being reported on.</param>
	/// <param name="concurrentSessions">
	///     Candidate sessions to intersect against. Sessions belonging to another worker, or to a node
	///     already present in <paramref name="subjectSessions" />, are ignored rather than rejected — a
	///     caller may legitimately hand over a superset.
	/// </param>
	/// <exception cref="ArgumentNullException">Either collection is <see langword="null" />.</exception>
	public static IReadOnlyList<ConcurrentWorkOverlap> Calculate(
		IReadOnlyCollection<ConcurrentWorkSession> subjectSessions,
		IReadOnlyCollection<ConcurrentWorkSession> concurrentSessions)
	{
		ArgumentNullException.ThrowIfNull(subjectSessions);
		ArgumentNullException.ThrowIfNull(concurrentSessions);

		if (subjectSessions.Count == 0 || concurrentSessions.Count == 0) {
			return [];
		}

		var subjectNodeIds = subjectSessions.Select(session => session.NodeId).ToHashSet();
		var subjectsByWorker = subjectSessions
			.GroupBy(session => session.WorkedByUserId)
			.ToDictionary(group => group.Key, IReadOnlyList<ConcurrentWorkSession> (group) => [.. group]);

		var accumulators = new Dictionary<(AppUserId Worker, JobNodeId Node), OverlapAccumulator>();
		foreach (var candidate in concurrentSessions) {
			if (subjectNodeIds.Contains(candidate.NodeId)
				|| !subjectsByWorker.TryGetValue(candidate.WorkedByUserId, out var workerSubjects)) {
				continue;
			}

			foreach (var subject in workerSubjects) {
				if (IntervalAlgebra.Intersect(subject.Interval, candidate.Interval) is not WorkInterval intersection) {
					continue;
				}

				var key = (candidate.WorkedByUserId, candidate.NodeId);
				accumulators[key] = accumulators.TryGetValue(key, out var accumulated)
					? accumulated.Add(intersection)
					: OverlapAccumulator.Of(intersection);
			}
		}

		var workerTotals = accumulators
			.GroupBy(entry => entry.Key.Worker)
			.ToDictionary(group => group.Key, group => group.Sum(entry => entry.Value.Total.TotalTicks));

		return [
			.. accumulators
				.OrderByDescending(entry => workerTotals[entry.Key.Worker])
				.ThenBy(entry => entry.Key.Worker.Value)
				.ThenByDescending(entry => entry.Value.Total)
				.ThenBy(entry => entry.Key.Node.Value)
				.Select(entry => new ConcurrentWorkOverlap(
					entry.Key.Worker, entry.Key.Node, entry.Value.Total, entry.Value.Count,
					entry.Value.FirstStart, entry.Value.LastEnd)),
		];
	}

	/// <summary>Running total, count, and extent of one (worker, node) pair's intersections.</summary>
	private readonly record struct OverlapAccumulator(Duration Total, int Count, Instant FirstStart, Instant LastEnd)
	{
		public static OverlapAccumulator Of(WorkInterval intersection) =>
			new(intersection.Duration, 1, intersection.Start, intersection.End);

		public OverlapAccumulator Add(WorkInterval intersection) =>
			new(Total + intersection.Duration, Count + 1,
				Instant.Min(FirstStart, intersection.Start), Instant.Max(LastEnd, intersection.End));
	}
}
