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
		var sorted = intervals.OrderBy(interval => interval.Start).ToArray();
		if (sorted.Length == 0) {
			return [];
		}

		var merged = new List<WorkInterval>(sorted.Length) { sorted[0] };
		foreach (var current in sorted.Skip(1)) {
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
		return [
			.. minuend.SelectMany(source => cuts.Aggregate(
				(IEnumerable<WorkInterval>)[source],
				(pieces, cut) => pieces.SelectMany(piece => SubtractOne(piece, cut)))),
		];
	}

	private static IEnumerable<WorkInterval> SubtractOne(WorkInterval piece, WorkInterval cut)
	{
		if (!Overlaps(piece, cut)) {
			yield return piece;
			yield break;
		}

		if (cut.Start > piece.Start) {
			yield return new(piece.Start, cut.Start);
		}

		if (cut.End < piece.End) {
			yield return new(cut.End, piece.End);
		}
	}
}
