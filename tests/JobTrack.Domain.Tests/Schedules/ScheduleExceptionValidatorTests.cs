namespace JobTrack.Domain.Tests.Schedules;

using Abstractions;
using AwesomeAssertions;
using Domain.Intervals;
using Domain.Schedules;
using NodaTime;

public sealed class ScheduleExceptionValidatorTests
{
	private static WorkInterval Interval(int startHour, int endHour) =>
		new(Instant.FromUtc(2024, 1, 1, startHour, 0), Instant.FromUtc(2024, 1, 1, endHour, 0));

	[Fact]
	public void Two_overlapping_priced_additive_exceptions_are_rejected()
	{
		var exceptions = new[] {
			new ScheduleExceptionEntry(ScheduleExceptionEffect.AddWorkingTime, Interval(18, 22), new HourlyRate(50m)),
			new ScheduleExceptionEntry(ScheduleExceptionEffect.AddWorkingTime, Interval(20, 23), new HourlyRate(75m)),
		};

		var act = () => ScheduleExceptionValidator.EnsureNoOverlappingPricedAdditiveExceptions(exceptions);

		act.Should().Throw<InvariantViolationException>();
	}

	[Fact]
	public void Two_adjacent_priced_additive_exceptions_are_accepted()
	{
		var exceptions = new[] {
			new ScheduleExceptionEntry(ScheduleExceptionEffect.AddWorkingTime, Interval(18, 20), new HourlyRate(50m)),
			new ScheduleExceptionEntry(ScheduleExceptionEffect.AddWorkingTime, Interval(20, 23), new HourlyRate(75m)),
		};

		var act = () => ScheduleExceptionValidator.EnsureNoOverlappingPricedAdditiveExceptions(exceptions);

		act.Should().NotThrow();
	}

	[Fact]
	public void Overlapping_unpriced_additive_exceptions_are_accepted()
	{
		var exceptions = new[] {
			new ScheduleExceptionEntry(ScheduleExceptionEffect.AddWorkingTime, Interval(18, 22), null),
			new ScheduleExceptionEntry(ScheduleExceptionEffect.AddWorkingTime, Interval(20, 23), null),
		};

		var act = () => ScheduleExceptionValidator.EnsureNoOverlappingPricedAdditiveExceptions(exceptions);

		act.Should().NotThrow();
	}

	[Fact]
	public void A_priced_additive_exception_overlapping_a_subtractive_exception_is_accepted()
	{
		var exceptions = new[] {
			new ScheduleExceptionEntry(ScheduleExceptionEffect.AddWorkingTime, Interval(18, 22), new HourlyRate(50m)),
			new ScheduleExceptionEntry(ScheduleExceptionEffect.RemoveWorkingTime, Interval(20, 23), null),
		};

		var act = () => ScheduleExceptionValidator.EnsureNoOverlappingPricedAdditiveExceptions(exceptions);

		act.Should().NotThrow();
	}

	[Fact]
	public void EnsureNoOverlappingPricedAdditiveExceptions_throws_for_an_unknown_effect_value()
	{
		var exceptions = new[] { ScheduleExceptionEntryTestSupport.WithEffect((ScheduleExceptionEffect)(-1), Interval(18, 22)) };

		var act = () => ScheduleExceptionValidator.EnsureNoOverlappingPricedAdditiveExceptions(exceptions);

		act.Should().Throw<ArgumentOutOfRangeException>();
	}
}
