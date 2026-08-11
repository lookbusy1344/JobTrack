namespace JobTrack.Domain.Tests.Costing;

using AwesomeAssertions;
using Domain.Costing;

public sealed class AllocatedShareTests
{
	[Fact]
	public void A_non_positive_segment_tick_count_throws()
	{
		var act = () => new AllocatedShare(0, 1);

		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void A_non_positive_concurrency_divisor_throws()
	{
		var act = () => new AllocatedShare(100, 0);

		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void Valid_values_are_retained_exactly_without_reducing_the_fraction()
	{
		var share = new AllocatedShare(100, 3);

		share.SegmentTicks.Should().Be(100);
		share.ConcurrencyDivisor.Should().Be(3);
	}

	[Fact]
	public void The_default_value_bypasses_the_constructor_and_reports_itself_uninitialized() =>
		default(AllocatedShare).IsUninitialized.Should().BeTrue();

	[Fact]
	public void A_constructed_value_is_not_uninitialized() => new AllocatedShare(100, 3).IsUninitialized.Should().BeFalse();
}
