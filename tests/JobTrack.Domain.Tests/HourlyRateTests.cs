namespace JobTrack.Domain.Tests;

using Abstractions;
using AwesomeAssertions;

/// <summary>See <see cref="MoneyTests" /> for why this Abstractions value type is tested here.</summary>
public sealed class HourlyRateTests
{
	[Fact]
	public void A_non_negative_rate_is_accepted()
	{
		var rate = new HourlyRate(18.5m);

		rate.AmountPerHour.Should().Be(18.5m);
	}

	[Fact]
	public void Zero_is_accepted()
	{
		var rate = new HourlyRate(0m);

		rate.AmountPerHour.Should().Be(0m);
	}

	[Fact]
	public void A_negative_rate_is_rejected()
	{
		var act = () => new HourlyRate(-1m);

		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void Two_instances_with_the_same_rate_are_equal() => new HourlyRate(9.5m).Should().Be(new HourlyRate(9.5m));
}
