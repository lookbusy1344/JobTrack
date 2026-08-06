namespace JobTrack.Domain.Tests.Intervals;

using AwesomeAssertions;
using Domain.Intervals;
using NodaTime;

public sealed class IntervalAlgebraTests
{
	private static Instant At(int hour) => Instant.FromUtc(2026, 1, 1, hour, 0);

	private static WorkInterval Between(int startHour, int endHour) => new(At(startHour), At(endHour));

	public sealed class Overlaps
	{
		[Fact]
		public void Intervals_that_merely_touch_do_not_overlap() => IntervalAlgebra.Overlaps(Between(9, 11), Between(11, 13)).Should().BeFalse();

		[Fact]
		public void Disjoint_intervals_do_not_overlap() => IntervalAlgebra.Overlaps(Between(9, 10), Between(11, 12)).Should().BeFalse();

		[Fact]
		public void Partially_overlapping_intervals_overlap() => IntervalAlgebra.Overlaps(Between(9, 12), Between(11, 13)).Should().BeTrue();

		[Fact]
		public void One_interval_wholly_containing_another_overlaps() => IntervalAlgebra.Overlaps(Between(9, 17), Between(11, 12)).Should().BeTrue();

		[Fact]
		public void Overlap_is_symmetric() => IntervalAlgebra.Overlaps(Between(11, 13), Between(9, 12)).Should().BeTrue();
	}

	public sealed class Intersect
	{
		[Fact]
		public void The_spec_example_intersects_to_the_shared_hour()
		{
			var result = IntervalAlgebra.Intersect(Between(9, 12), Between(11, 13));

			result.Should().Be(Between(11, 12));
		}

		[Fact]
		public void Touching_intervals_have_no_intersection() => IntervalAlgebra.Intersect(Between(9, 11), Between(11, 13)).Should().BeNull();

		[Fact]
		public void Disjoint_intervals_have_no_intersection() => IntervalAlgebra.Intersect(Between(9, 10), Between(11, 12)).Should().BeNull();

		[Fact]
		public void An_interval_wholly_inside_another_intersects_to_itself() =>
			IntervalAlgebra.Intersect(Between(9, 17), Between(11, 12)).Should().Be(Between(11, 12));
	}

	public sealed class Clip
	{
		[Fact]
		public void An_interval_wholly_inside_the_bound_is_unchanged()
		{
			var result = IntervalAlgebra.Clip([Between(10, 11)], Between(9, 17));

			result.Should().Equal(Between(10, 11));
		}

		[Fact]
		public void An_interval_wholly_outside_the_bound_is_dropped()
		{
			var result = IntervalAlgebra.Clip([Between(1, 2)], Between(9, 17));

			result.Should().BeEmpty();
		}

		[Fact]
		public void An_interval_crossing_the_bound_start_is_trimmed()
		{
			var result = IntervalAlgebra.Clip([Between(7, 10)], Between(9, 17));

			result.Should().Equal(Between(9, 10));
		}

		[Fact]
		public void An_interval_crossing_the_bound_end_is_trimmed()
		{
			var result = IntervalAlgebra.Clip([Between(16, 20)], Between(9, 17));

			result.Should().Equal(Between(16, 17));
		}

		[Fact]
		public void An_interval_only_touching_the_bound_is_dropped()
		{
			var result = IntervalAlgebra.Clip([Between(5, 9)], Between(9, 17));

			result.Should().BeEmpty();
		}

		[Fact]
		public void Multiple_intervals_are_each_clipped_independently()
		{
			var result = IntervalAlgebra.Clip([Between(7, 10), Between(16, 20), Between(1, 2)], Between(9, 17));

			result.Should().Equal(Between(9, 10), Between(16, 17));
		}
	}

	public sealed class Normalize
	{
		[Fact]
		public void A_single_interval_is_returned_unchanged()
		{
			var result = IntervalAlgebra.Normalize([Between(9, 12)]);

			result.Should().Equal(Between(9, 12));
		}

		[Fact]
		public void Unsorted_disjoint_intervals_are_sorted()
		{
			var result = IntervalAlgebra.Normalize([Between(14, 15), Between(9, 10)]);

			result.Should().Equal(Between(9, 10), Between(14, 15));
		}

		[Fact]
		public void Overlapping_intervals_are_merged_into_their_union()
		{
			var result = IntervalAlgebra.Normalize([Between(9, 12), Between(11, 14)]);

			result.Should().Equal(Between(9, 14));
		}

		[Fact]
		public void Adjacent_touching_intervals_are_merged_so_no_instant_is_double_counted()
		{
			var result = IntervalAlgebra.Normalize([Between(9, 11), Between(11, 13)]);

			result.Should().Equal(Between(9, 13));
		}

		[Fact]
		public void Duplicate_identical_intervals_collapse_to_one()
		{
			var result = IntervalAlgebra.Normalize([Between(9, 12), Between(9, 12)]);

			result.Should().Equal(Between(9, 12));
		}

		[Fact]
		public void An_interval_wholly_contained_in_another_is_absorbed()
		{
			var result = IntervalAlgebra.Normalize([Between(9, 17), Between(11, 12)]);

			result.Should().Equal(Between(9, 17));
		}

		[Fact]
		public void An_empty_input_produces_an_empty_result()
		{
			var result = IntervalAlgebra.Normalize([]);

			result.Should().BeEmpty();
		}

		[Fact]
		public void Three_intervals_chain_merge_into_one_union()
		{
			var result = IntervalAlgebra.Normalize([Between(9, 11), Between(15, 17), Between(10, 16)]);

			result.Should().Equal(Between(9, 17));
		}
	}

	public sealed class Subtract
	{
		[Fact]
		public void Subtracting_a_disjoint_interval_leaves_the_minuend_unchanged()
		{
			var result = IntervalAlgebra.Subtract([Between(9, 17)], [Between(18, 19)]);

			result.Should().Equal(Between(9, 17));
		}

		[Fact]
		public void Subtracting_a_middle_slice_splits_the_interval_in_two()
		{
			var result = IntervalAlgebra.Subtract([Between(9, 17)], [Between(12, 13)]);

			result.Should().Equal(Between(9, 12), Between(13, 17));
		}

		[Fact]
		public void Subtracting_a_prefix_leaves_the_remaining_suffix()
		{
			var result = IntervalAlgebra.Subtract([Between(9, 17)], [Between(8, 12)]);

			result.Should().Equal(Between(12, 17));
		}

		[Fact]
		public void Subtracting_a_suffix_leaves_the_remaining_prefix()
		{
			var result = IntervalAlgebra.Subtract([Between(9, 17)], [Between(15, 20)]);

			result.Should().Equal(Between(9, 15));
		}

		[Fact]
		public void Subtracting_the_whole_interval_leaves_nothing()
		{
			var result = IntervalAlgebra.Subtract([Between(9, 17)], [Between(8, 20)]);

			result.Should().BeEmpty();
		}

		[Fact]
		public void Subtracting_an_interval_that_only_touches_the_boundary_leaves_it_unchanged()
		{
			var result = IntervalAlgebra.Subtract([Between(9, 17)], [Between(17, 20)]);

			result.Should().Equal(Between(9, 17));
		}

		[Fact]
		public void Multiple_subtrahends_remove_multiple_slices()
		{
			var result = IntervalAlgebra.Subtract([Between(9, 17)], [Between(10, 11), Between(14, 15)]);

			result.Should().Equal(Between(9, 10), Between(11, 14), Between(15, 17));
		}

		[Fact]
		public void Multiple_minuend_intervals_are_each_reduced_independently()
		{
			var result = IntervalAlgebra.Subtract([Between(9, 12), Between(14, 17)], [Between(10, 15)]);

			result.Should().Equal(Between(9, 10), Between(15, 17));
		}

		[Fact]
		public void An_empty_subtrahend_leaves_the_minuend_unchanged()
		{
			var result = IntervalAlgebra.Subtract([Between(9, 17)], []);

			result.Should().Equal(Between(9, 17));
		}

		/// <summary>
		///     2026-08-06-cost-read-materialisation-reduction-plan.md Stage 4: reproduces the long-history
		///     scale's own shape (one worker's 5-year daily schedule resolved against 5 years of daily
		///     schedule exceptions, performance-budgets.md §1) at the domain layer, in isolation from any
		///     database. The original O(minuend x cuts) implementation measured 378.5 ms for this input
		///     (a standalone microbenchmark, 2026-08-06); a linear-time implementation over the same,
		///     already-sorted-and-disjoint inputs must run in a small fraction of that. The ceiling is
		///     deliberately generous (measured post-fix: ~10 ms): this test runs in the parallelized fast
		///     lane, not the serialized perf lane that is the accepted evidence channel for a tight
		///     ceiling, so it only needs to separate quadratic (~400 ms) from linear under CI contention.
		/// </summary>
		[Fact]
		public void Resolving_a_five_year_daily_schedule_against_five_years_of_daily_exceptions_stays_fast()
		{
			const int days = 5 * 365;
			var scheduled = new List<WorkInterval>(days);
			var exceptions = new List<WorkInterval>(days);
			for (var day = 0; day < days; ++day) {
				var dayStart = Instant.FromUtc(2021, 1, 1, 0, 0) + Duration.FromDays(day);
				scheduled.Add(new(dayStart, dayStart + Duration.FromHours(23) + Duration.FromMinutes(59) + Duration.FromSeconds(59)));
				var exceptionStart = dayStart + Duration.FromHours(12);
				exceptions.Add(new(exceptionStart, exceptionStart + Duration.FromHours(1)));
			}

			var stopwatch = System.Diagnostics.Stopwatch.StartNew();
			var result = IntervalAlgebra.Subtract(scheduled, exceptions);
			stopwatch.Stop();

			result.Should().HaveCount(days * 2);
			stopwatch.Elapsed.Should().BeLessThan(
				TimeSpan.FromMilliseconds(200), "Subtract must not be quadratic in already-sorted, disjoint minuend/cuts counts");
		}
	}
}
