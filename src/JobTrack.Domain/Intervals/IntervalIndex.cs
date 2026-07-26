namespace JobTrack.Domain.Intervals;

/// <summary>
///     A search structure over a fixed set of <see cref="WorkInterval" />s, built once and queried many
///     times. Replaces the repeated linear scans the cost engine previously paid per costed segment and
///     per session: both of those scan lengths grow with the costed window (a worker's schedule expands
///     to one interval per working day between their earliest session and <c>asOf</c>), so a linear
///     probe made an ageing installation progressively slower at answering an unchanged question.
///     <para>
///         The production ports always supply a normalized — sorted and disjoint — set, which admits a
///         binary search. <see cref="Costing.CostSegmentPartitioner" /> and
///         <see cref="Costing.CostEngine" /> are public API, though, and cannot require that of a
///         caller, so <see cref="Build" /> detects disjointness once and every query falls back to a
///         full scan when it does not hold. Results are identical either way; only the cost differs.
///     </para>
/// </summary>
internal sealed class IntervalIndex
{
	private readonly bool isDisjoint;
	private readonly WorkInterval[] sorted;

	private IntervalIndex(WorkInterval[] sorted, bool isDisjoint)
	{
		this.sorted = sorted;
		this.isDisjoint = isDisjoint;
	}

	/// <summary>Indexes <paramref name="intervals" />, sorting a copy rather than mutating the caller's collection.</summary>
	public static IntervalIndex Build(IReadOnlyCollection<WorkInterval> intervals)
	{
		var sorted = new WorkInterval[intervals.Count];
		var next = 0;
		foreach (var interval in intervals) {
			sorted[next++] = interval;
		}

		Array.Sort(sorted, static (left, right) => left.Start.CompareTo(right.Start));

		var isDisjoint = true;
		for (var index = 1; index < sorted.Length; ++index) {
			if (sorted[index].Start < sorted[index - 1].End) {
				isDisjoint = false;
				break;
			}
		}

		return new(sorted, isDisjoint);
	}

	/// <summary>Whether any indexed interval shares an instant with <paramref name="query" />.</summary>
	public bool Intersects(WorkInterval query)
	{
		if (!isDisjoint) {
			foreach (var interval in sorted) {
				if (IntervalAlgebra.Overlaps(query, interval)) {
					return true;
				}
			}

			return false;
		}

		var first = FirstPossiblyOverlappingIndex(query);
		return first < sorted.Length && sorted[first].Start < query.End;
	}

	/// <summary>
	///     Every indexed interval sharing an instant with <paramref name="query" />, in ascending start
	///     order. A disjoint index walks only the matching run; otherwise every interval is tested.
	/// </summary>
	public IEnumerable<WorkInterval> Overlapping(WorkInterval query)
	{
		if (!isDisjoint) {
			foreach (var interval in sorted) {
				if (IntervalAlgebra.Overlaps(query, interval)) {
					yield return interval;
				}
			}

			yield break;
		}

		for (var index = FirstPossiblyOverlappingIndex(query); index < sorted.Length; ++index) {
			var interval = sorted[index];
			if (interval.Start >= query.End) {
				yield break;
			}

			yield return interval;
		}
	}

	/// <summary>
	///     The first index whose interval ends strictly after <paramref name="query" />'s start. Valid
	///     only for a disjoint index, where sorting by start also sorts by end, making the predicate
	///     monotonic and so binary-searchable.
	/// </summary>
	private int FirstPossiblyOverlappingIndex(WorkInterval query)
	{
		var low = 0;
		var high = sorted.Length;
		while (low < high) {
			var middle = low + ((high - low) / 2);
			if (sorted[middle].End <= query.Start) {
				low = middle + 1;
			} else {
				high = middle;
			}
		}

		return low;
	}
}
