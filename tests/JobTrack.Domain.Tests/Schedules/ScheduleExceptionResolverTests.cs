namespace JobTrack.Domain.Tests.Schedules;

using AwesomeAssertions;
using Domain.Intervals;
using Domain.Schedules;
using NodaTime;

public sealed class ScheduleExceptionResolverTests
{
	private static WorkInterval Interval(int startHour, int endHour) =>
		new(Instant.FromUtc(2024, 1, 1, startHour, 0), Instant.FromUtc(2024, 1, 1, endHour, 0));

	[Fact]
	public void With_no_exceptions_the_scheduled_intervals_pass_through_normalized()
	{
		var scheduled = new[] { Interval(9, 12), Interval(12, 17) };

		var effective = ScheduleExceptionResolver.Apply(scheduled, []);

		effective.Should().BeEquivalentTo([Interval(9, 17)]);
	}

	[Fact]
	public void An_additive_exception_extends_the_effective_working_set()
	{
		var scheduled = new[] { Interval(9, 17) };
		var exceptions = new[] { new ScheduleExceptionEntry(ScheduleExceptionEffect.AddWorkingTime, Interval(17, 20), null) };

		var effective = ScheduleExceptionResolver.Apply(scheduled, exceptions);

		effective.Should().BeEquivalentTo([Interval(9, 20)]);
	}

	[Fact]
	public void A_subtractive_exception_removes_scheduled_time()
	{
		var scheduled = new[] { Interval(9, 17) };
		var exceptions = new[] { new ScheduleExceptionEntry(ScheduleExceptionEffect.RemoveWorkingTime, Interval(12, 13), null) };

		var effective = ScheduleExceptionResolver.Apply(scheduled, exceptions);

		effective.Should().BeEquivalentTo([Interval(9, 12), Interval(13, 17)]);
	}

	[Fact]
	public void A_subtractive_exception_takes_precedence_over_an_overlapping_additive_exception()
	{
		var scheduled = new[] { Interval(9, 17) };
		var exceptions = new[] {
			new ScheduleExceptionEntry(ScheduleExceptionEffect.AddWorkingTime, Interval(17, 22), null),
			new ScheduleExceptionEntry(ScheduleExceptionEffect.RemoveWorkingTime, Interval(18, 20), null),
		};

		var effective = ScheduleExceptionResolver.Apply(scheduled, exceptions);

		effective.Should().BeEquivalentTo([Interval(9, 18), Interval(20, 22)]);
	}

	[Fact]
	public void A_subtractive_exception_can_remove_all_scheduled_time_leaving_only_additive_time()
	{
		var scheduled = new[] { Interval(9, 17) };
		var exceptions = new[] { new ScheduleExceptionEntry(ScheduleExceptionEffect.RemoveWorkingTime, Interval(9, 17), null) };

		var effective = ScheduleExceptionResolver.Apply(scheduled, exceptions);

		effective.Should().BeEmpty();
	}

	[Fact]
	public void Apply_throws_for_an_unknown_effect_value()
	{
		var scheduled = new[] { Interval(9, 17) };
		var exceptions = new[] { ScheduleExceptionEntryTestSupport.WithEffect((ScheduleExceptionEffect)(-1), Interval(12, 13)) };

		var act = () => ScheduleExceptionResolver.Apply(scheduled, exceptions);

		act.Should().Throw<ArgumentOutOfRangeException>();
	}
}
