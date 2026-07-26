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

	[Fact]
	public void Formatting_renders_sterling_per_hour() => new HourlyRate(18.5m).ToString().Should().Be("£18.50/hr");

	[Fact]
	public void Formatting_honours_an_explicit_numeric_format() => new HourlyRate(18.5m).ToString("N4", null).Should().Be("£18.5000/hr");

	[Fact]
	public void A_smaller_rate_compares_below_a_larger_one() => new HourlyRate(9m).CompareTo(new HourlyRate(11m)).Should().BeNegative();

	[Fact]
	public void The_comparison_operators_order_rates()
	{
		(new HourlyRate(9m) < new HourlyRate(11m)).Should().BeTrue();
		(new HourlyRate(11m) > new HourlyRate(9m)).Should().BeTrue();
		(new HourlyRate(9m) <= new HourlyRate(9m)).Should().BeTrue();
		(new HourlyRate(9m) >= new HourlyRate(9m)).Should().BeTrue();
	}
}
