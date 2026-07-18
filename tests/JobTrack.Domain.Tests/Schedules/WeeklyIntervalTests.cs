namespace JobTrack.Domain.Tests.Schedules;

using AwesomeAssertions;
using Domain.Schedules;
using NodaTime;

public sealed class WeeklyIntervalTests
{
	[Fact]
	public void An_interval_within_one_day_does_not_cross_midnight()
	{
		var interval = new WeeklyInterval(IsoDayOfWeek.Monday, new(9, 0), new(17, 0));

		interval.CrossesMidnight.Should().BeFalse();
	}

	[Fact]
	public void An_interval_whose_end_is_before_its_start_crosses_midnight()
	{
		var interval = new WeeklyInterval(IsoDayOfWeek.Saturday, new(22, 0), new(2, 0));

		interval.CrossesMidnight.Should().BeTrue();
	}

	[Fact]
	public void Constructing_with_no_day_throws()
	{
		var act = () => new WeeklyInterval(IsoDayOfWeek.None, new(9, 0), new(17, 0));

		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void Constructing_with_equal_start_and_end_throws()
	{
		var act = () => new WeeklyInterval(IsoDayOfWeek.Monday, new(9, 0), new(9, 0));

		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void Constructing_with_a_sub_second_start_throws()
	{
		var act = () => new WeeklyInterval(IsoDayOfWeek.Monday, new LocalTime(9, 0, 0).PlusTicks(1), new(17, 0));

		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void Constructing_with_a_sub_second_end_throws()
	{
		var act = () => new WeeklyInterval(IsoDayOfWeek.Monday, new(9, 0), new LocalTime(17, 0, 0).PlusTicks(1));

		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void Constructing_with_whole_second_boundaries_succeeds()
	{
		var interval = new WeeklyInterval(IsoDayOfWeek.Monday, new(9, 0, 30), new(17, 0, 45));

		interval.Start.Should().Be(new(9, 0, 30));
	}
}
