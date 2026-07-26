namespace JobTrack.Domain.Tests.Intervals;

using AwesomeAssertions;
using Domain.Intervals;
using NodaTime;

public sealed class IntervalIndexTests
{
	private static Instant At(int hour, int minute = 0) => Instant.FromUtc(2026, 1, 1, hour, minute);

	private static WorkInterval Between(int startHour, int endHour) => new(At(startHour), At(endHour));

	public sealed class DisjointIndex
	{
		private static readonly IntervalIndex Index = IntervalIndex.Build([Between(9, 10), Between(12, 13), Between(15, 17)]);

		[Fact]
		public void A_query_before_every_interval_does_not_intersect() => Index.Intersects(Between(1, 2)).Should().BeFalse();

		[Fact]
		public void A_query_after_every_interval_does_not_intersect() => Index.Intersects(Between(20, 21)).Should().BeFalse();

		[Fact]
		public void A_query_between_two_indexed_intervals_does_not_intersect() => Index.Intersects(Between(10, 12)).Should().BeFalse();

		[Fact]
		public void A_query_overlapping_the_first_interval_intersects() => Index.Intersects(new(At(8), At(9, 1))).Should().BeTrue();

		[Fact]
		public void A_query_overlapping_the_last_interval_intersects() => Index.Intersects(Between(16, 18)).Should().BeTrue();

		[Fact]
		public void A_query_wholly_containing_a_middle_interval_intersects() => Index.Intersects(Between(11, 14)).Should().BeTrue();

		[Fact]
		public void A_query_that_only_touches_an_interval_boundary_does_not_intersect() => Index.Intersects(new(At(10), At(12))).Should().BeFalse();

		[Fact]
		public void Overlapping_returns_only_the_matching_run_in_ascending_order()
		{
			var result = Index.Overlapping(new(At(9, 30), At(16)));

			result.Should().Equal(Between(9, 10), Between(12, 13), Between(15, 17));
		}

		[Fact]
		public void Overlapping_returns_nothing_for_a_query_that_matches_no_interval() => Index.Overlapping(Between(10, 12)).Should().BeEmpty();

		[Fact]
		public void Overlapping_stops_scanning_once_past_the_last_matching_interval() =>
			Index.Overlapping(Between(9, 10)).Should().Equal(Between(9, 10));

		[Fact]
		public void An_empty_index_never_intersects() => IntervalIndex.Build([]).Intersects(Between(1, 2)).Should().BeFalse();

		[Fact]
		public void An_empty_index_has_no_overlaps() => IntervalIndex.Build([]).Overlapping(Between(1, 2)).Should().BeEmpty();

		[Fact]
		public void Intervals_are_indexed_regardless_of_input_order()
		{
			var reversed = IntervalIndex.Build([Between(15, 17), Between(9, 10), Between(12, 13)]);

			reversed.Overlapping(Between(1, 20)).Should().Equal(Between(9, 10), Between(12, 13), Between(15, 17));
		}
	}

	public sealed class NonDisjointIndex
	{
		private static readonly IntervalIndex Index = IntervalIndex.Build([Between(9, 13), Between(11, 15)]);

		[Fact]
		public void Overlapping_input_is_detected_as_non_disjoint_and_still_answers_correctly() =>
			Index.Intersects(Between(14, 16)).Should().BeTrue();

		[Fact]
		public void A_query_missing_both_overlapping_intervals_does_not_intersect() => Index.Intersects(Between(20, 21)).Should().BeFalse();

		[Fact]
		public void Overlapping_returns_every_interval_that_matches_not_only_the_first() =>
			Index.Overlapping(new(At(12), At(12, 1))).Should().Equal(Between(9, 13), Between(11, 15));
	}
}
