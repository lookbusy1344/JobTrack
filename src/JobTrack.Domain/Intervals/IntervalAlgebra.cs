namespace JobTrack.Domain.Intervals;

/// <summary>
///     Pure half-open interval algebra (spec §7.2 step 2, §8.2, §10.2.1, §10.3): overlap detection,
///     pairwise intersection, clipping a set to a bound, normalizing a set to its minimal disjoint
///     union, and set subtraction. No I/O, no mutable state.
/// </summary>
public static class IntervalAlgebra
{
	/// <summary>
	///     Whether two intervals share any instant. Intervals that merely touch at a boundary — one
	///     ending exactly when the other starts — do not overlap (spec §10.2.1).
	/// </summary>
	public static bool Overlaps(WorkInterval a, WorkInterval b) => a.Start < b.End && b.Start < a.End;

	/// <summary>
	///     The shared portion of two intervals, or <see langword="null" /> when they do not overlap.
	/// </summary>
	public static WorkInterval? Intersect(WorkInterval a, WorkInterval b)
	{
		var start = a.Start > b.Start ? a.Start : b.Start;
		var end = a.End < b.End ? a.End : b.End;
		return end > start ? new WorkInterval(start, end) : null;
	}

	/// <summary>
	///     Clips every interval to <paramref name="bounds" /> (e.g. a reporting range or <c>asOf</c>,
	///     spec §10.3 step 5), dropping any interval left with no overlap.
	/// </summary>
	public static IReadOnlyList<WorkInterval> Clip(IEnumerable<WorkInterval> intervals, WorkInterval bounds) => [
		.. intervals
			.Select(interval => Intersect(interval, bounds))
			.Where(clipped => clipped.HasValue)
			.Select(clipped => clipped!.Value),
	];

	/// <summary>
	///     Merges overlapping and adjacent (touching) intervals into their minimal sorted, disjoint
	///     union, so no instant is counted twice (spec §8.2).
	/// </summary>
	public static IReadOnlyList<WorkInterval> Normalize(IEnumerable<WorkInterval> intervals)
	{
		// Sorted in place rather than through OrderBy: this runs against every worker's full schedule
		// per cost read, and the enumerator/buffer churn was measurable. Array.Sort's instability does
		// not matter here — equal-start intervals merge to the same result in either order.
		var sorted = intervals.ToArray();
		Array.Sort(sorted, static (left, right) => left.Start.CompareTo(right.Start));
		if (sorted.Length == 0) {
			return [];
		}

		var merged = new List<WorkInterval>(sorted.Length) { sorted[0] };
		foreach (var current in sorted.AsSpan(1)) {
			var last = merged[^1];
			if (current.Start > last.End) {
				merged.Add(current);
			} else if (current.End > last.End) {
				merged[^1] = new(last.Start, current.End);
			}
		}

		return merged;
	}

	/// <summary>
	///     Removes every instant covered by <paramref name="subtrahend" /> from every interval in
	///     <paramref name="minuend" /> (e.g. subtractive schedule exceptions taking precedence over
	///     additive ones, spec §8.2/§10.3), splitting a minuend interval where a subtrahend interval
	///     falls strictly inside it.
	/// </summary>
	public static IReadOnlyList<WorkInterval> Subtract(IEnumerable<WorkInterval> minuend, IEnumerable<WorkInterval> subtrahend)
	{
		var cuts = Normalize(subtrahend);
		if (cuts.Count == 0) {
			return [.. minuend];
		}

		// Testing every minuend interval against every cut is O(minuend x cuts) even though `cuts` is
		// already sorted and disjoint (Normalize's own postcondition) -- exactly the shape IntervalIndex
		// exists to search in better-than-linear time (2026-08-06-cost-read-materialisation-reduction-
		// plan.md Stage 4: this scaled to 378.5 ms resolving one worker's 5-year daily schedule against
		// 5 years of daily exceptions). `Overlapping` walks only the cuts that could actually intersect
		// each minuend interval, in ascending start order, which lets one cursor sweep each source
		// front to back emitting the uncovered gaps directly -- no per-source enumerator chain.
		var cutsIndex = IntervalIndex.Build(cuts);
		var result = new List<WorkInterval>();
		foreach (var source in minuend) {
			var uncoveredFrom = source.Start;
			foreach (var cut in cutsIndex.Overlapping(source)) {
				if (cut.Start > uncoveredFrom) {
					result.Add(new(uncoveredFrom, cut.Start));
				}

				if (cut.End >= source.End) {
					uncoveredFrom = source.End;
					break;
				}

				if (cut.End > uncoveredFrom) {
					uncoveredFrom = cut.End;
				}
			}

			if (uncoveredFrom < source.End) {
				result.Add(new(uncoveredFrom, source.End));
			}
		}

		return result;
	}
}
