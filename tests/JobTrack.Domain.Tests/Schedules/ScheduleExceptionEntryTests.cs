namespace JobTrack.Domain.Tests.Schedules;

using Abstractions;
using AwesomeAssertions;
using Domain.Intervals;
using Domain.Schedules;
using NodaTime;

public sealed class ScheduleExceptionTests
{
	private static readonly WorkInterval Interval = new(Instant.FromUtc(2024, 1, 1, 9, 0), Instant.FromUtc(2024, 1, 1, 17, 0));

	[Fact]
	public void An_additive_exception_may_carry_a_rate_override()
	{
		var exception = new ScheduleExceptionEntry(ScheduleExceptionEffect.AddWorkingTime, Interval, new HourlyRate(50m));

		exception.RateOverride.Should().Be(new HourlyRate(50m));
	}

	[Fact]
	public void An_additive_exception_may_omit_a_rate_override()
	{
		var exception = new ScheduleExceptionEntry(ScheduleExceptionEffect.AddWorkingTime, Interval, null);

		exception.RateOverride.Should().BeNull();
	}

	[Fact]
	public void A_subtractive_exception_cannot_carry_a_rate_override()
	{
		var act = () => new ScheduleExceptionEntry(ScheduleExceptionEffect.RemoveWorkingTime, Interval, new HourlyRate(50m));

		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void The_unspecified_effect_is_rejected()
	{
		var act = () => new ScheduleExceptionEntry(ScheduleExceptionEffect.None, Interval, null);

		act.Should().Throw<ArgumentOutOfRangeException>();
	}
}
