namespace JobTrack.Domain.Intervals;

using System.Collections;

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

		var first = FirstPossiblyOverlappingIndex(sorted, query);
		return first < sorted.Length && sorted[first].Start < query.End;
	}

	/// <summary>
	///     Every indexed interval sharing an instant with <paramref name="query" />, in ascending start
	///     order. A disjoint index walks only the matching run; otherwise every interval is tested.
	///     Returns a struct enumerable (Stage 2 item 2f, large-database performance plan §4): a
	///     `foreach` over the concrete <see cref="OverlappingEnumerable" /> allocates no iterator
	///     state machine, which matters here because this is called once per costed session -- 36,500
	///     times at the long-history scale. `IEnumerable&lt;WorkInterval&gt;` is still implemented for
	///     LINQ/test-assertion call sites, which box the struct only on that fallback path.
	/// </summary>
	public OverlappingEnumerable Overlapping(WorkInterval query) => new(sorted, isDisjoint, query);

	/// <summary>
	///     The first index whose interval ends strictly after <paramref name="query" />'s start. Valid
	///     only for a disjoint index, where sorting by start also sorts by end, making the predicate
	///     monotonic and so binary-searchable.
	/// </summary>
	private static int FirstPossiblyOverlappingIndex(WorkInterval[] sorted, WorkInterval query)
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

	/// <summary>A zero-allocation `foreach` source over <see cref="IntervalIndex.Overlapping" />'s results; see that member's remarks.</summary>
	public readonly struct OverlappingEnumerable : IEnumerable<WorkInterval>
	{
		private readonly WorkInterval[] sorted;
		private readonly bool isDisjoint;
		private readonly WorkInterval query;

		internal OverlappingEnumerable(WorkInterval[] sorted, bool isDisjoint, WorkInterval query)
		{
			this.sorted = sorted;
			this.isDisjoint = isDisjoint;
			this.query = query;
		}

		public Enumerator GetEnumerator() => new(sorted, isDisjoint, query);

		IEnumerator<WorkInterval> IEnumerable<WorkInterval>.GetEnumerator() => GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

		/// <summary>The struct enumerator itself -- boxed only when reached through <see cref="IEnumerator{T}" />, never on a direct `foreach`.</summary>
		public struct Enumerator : IEnumerator<WorkInterval>
		{
			private readonly WorkInterval[] sorted;
			private readonly bool isDisjoint;
			private readonly WorkInterval query;
			private int index;
			private bool exhausted;

			internal Enumerator(WorkInterval[] sorted, bool isDisjoint, WorkInterval query)
			{
				this.sorted = sorted;
				this.isDisjoint = isDisjoint;
				this.query = query;
				index = isDisjoint ? FirstPossiblyOverlappingIndex(sorted, query) : 0;
				exhausted = false;
				Current = default;
			}

			public WorkInterval Current { get; private set; }

			readonly object IEnumerator.Current => Current;

			public bool MoveNext()
			{
				if (exhausted) {
					return false;
				}

				if (isDisjoint) {
					if (index >= sorted.Length || sorted[index].Start >= query.End) {
						exhausted = true;
						return false;
					}

					Current = sorted[index];
					++index;
					return true;
				}

				while (index < sorted.Length) {
					var interval = sorted[index];
					++index;
					if (IntervalAlgebra.Overlaps(query, interval)) {
						Current = interval;
						return true;
					}
				}

				exhausted = true;
				return false;
			}

			public readonly void Reset() => throw new NotSupportedException();

			public readonly void Dispose()
			{
			}
		}
	}
}
