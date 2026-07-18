namespace JobTrack.Domain.Tests.Costing;

using Abstractions;
using AwesomeAssertions;
using Domain.Costing;
using NodaTime;

public sealed class SegmentCostCalculatorTests
{
	private static readonly long OneHourTicks = Duration.FromHours(1).BclCompatibleTicks;

	[Fact]
	public void A_sole_active_session_receives_the_full_undivided_amount()
	{
		var share = new AllocatedShare(OneHourTicks, 1);

		SegmentCostCalculator.Calculate(share, new(60m)).Should().Be(new Money(60m));
	}

	[Fact]
	public void Two_concurrent_sessions_each_receive_exactly_half()
	{
		var share = new AllocatedShare(OneHourTicks, 2);

		SegmentCostCalculator.Calculate(share, new(60m)).Should().Be(new Money(30m));
	}

	[Fact]
	public void A_non_dividing_concurrency_produces_the_single_division_result_without_intermediate_rounding()
	{
		// 10/3 does not terminate; the formula must not pre-round the 1/3-hour share before
		// multiplying by the rate (which would give a different, incorrect result).
		var share = new AllocatedShare(OneHourTicks, 3);

		SegmentCostCalculator.Calculate(share, new(10m)).Should().Be(new Money(3.3333333333333333333333333333m));
	}

	[Fact]
	public void A_half_hour_segment_allocates_half_the_hourly_rate()
	{
		var share = new AllocatedShare(OneHourTicks / 2, 1);

		SegmentCostCalculator.Calculate(share, new(60m)).Should().Be(new Money(30m));
	}
}
