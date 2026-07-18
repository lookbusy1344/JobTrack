namespace JobTrack.Domain.Tests.Intervals;

using AwesomeAssertions;
using Domain.Intervals;
using NodaTime;

public sealed class WorkIntervalTests
{
	private static Instant At(int hour) => Instant.FromUtc(2026, 1, 1, hour, 0);

	[Fact]
	public void A_start_strictly_before_end_is_accepted()
	{
		var interval = new WorkInterval(At(9), At(12));

		interval.Start.Should().Be(At(9));
		interval.End.Should().Be(At(12));
	}

	[Fact]
	public void An_end_equal_to_start_is_rejected()
	{
		var act = () => new WorkInterval(At(9), At(9));

		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void An_end_before_start_is_rejected()
	{
		var act = () => new WorkInterval(At(12), At(9));

		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void Duration_is_end_minus_start()
	{
		var interval = new WorkInterval(At(9), At(12));

		interval.Duration.Should().Be(Duration.FromHours(3));
	}

	[Fact]
	public void Contains_the_start_instant()
	{
		var interval = new WorkInterval(At(9), At(12));

		interval.Contains(At(9)).Should().BeTrue();
	}

	[Fact]
	public void Does_not_contain_the_end_instant()
	{
		var interval = new WorkInterval(At(9), At(12));

		interval.Contains(At(12)).Should().BeFalse();
	}

	[Fact]
	public void Contains_an_instant_strictly_inside()
	{
		var interval = new WorkInterval(At(9), At(12));

		interval.Contains(At(10)).Should().BeTrue();
	}

	[Fact]
	public void Does_not_contain_an_instant_before_the_start()
	{
		var interval = new WorkInterval(At(9), At(12));

		interval.Contains(At(8)).Should().BeFalse();
	}

	[Fact]
	public void Two_instances_with_the_same_bounds_are_equal() => new WorkInterval(At(9), At(12)).Should().Be(new WorkInterval(At(9), At(12)));
}
