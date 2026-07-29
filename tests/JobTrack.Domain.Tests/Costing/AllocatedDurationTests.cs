namespace JobTrack.Domain.Tests.Costing;

using AwesomeAssertions;
using Domain.Costing;
using NodaTime;

public sealed class AllocatedDurationTests
{
	private const long HalfMicroHourTicks = 18_000;
	private const long OneAndAHalfMicroHourTicks = 54_000;
	private static readonly long OneHourTicks = Duration.FromHours(1).BclCompatibleTicks;

	[Fact]
	public void Zero_has_no_allocated_hours() => AllocatedDuration.Zero.ToHours().Should().Be(0m);

	[Fact]
	public void Three_exact_thirds_conserve_one_hour()
	{
		var third = AllocatedDuration.FromShare(new(OneHourTicks, 3));

		var total = third.Add(third).Add(third);

		total.Should().Be(AllocatedDuration.FromShare(new(OneHourTicks, 1)));
		total.ToHours().Should().Be(1m);
	}

	[Fact]
	public void Conversion_to_hours_rounds_once_to_six_decimal_places_using_midpoint_to_even()
	{
		var roundsDownToEven = AllocatedDuration.FromShare(new(HalfMicroHourTicks, 1));
		var roundsUpToEven = AllocatedDuration.FromShare(new(OneAndAHalfMicroHourTicks, 1));

		roundsDownToEven.ToHours().Should().Be(0m);
		roundsUpToEven.ToHours().Should().Be(0.000002m);
	}

	[Fact]
	public void An_uninitialized_share_is_rejected()
	{
		var act = () => AllocatedDuration.FromShare(default);

		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void Formatting_uses_one_invariant_decimal_place() =>
		AllocatedDuration.FromShare(new(OneHourTicks * 3, 2)).ToString().Should().Be("1.5 hrs");

	[Fact]
	public void Formatting_keeps_one_decimal_place_for_whole_hours() =>
		AllocatedDuration.FromShare(new(OneHourTicks, 1)).ToString().Should().Be("1.0 hrs");

	[Fact]
	public void Formatting_rounds_to_one_decimal_place_using_midpoint_to_even() =>
		AllocatedDuration.FromShare(new(OneHourTicks * 27, 20)).ToString().Should().Be("1.4 hrs");

	[Fact]
	public void Formatting_does_not_expose_repeating_reporting_decimals() =>
		AllocatedDuration.FromShare(new(OneHourTicks, 3)).ToString().Should().Be("0.3 hrs");
}
