namespace JobTrack.Domain.Tests.Rates;

using Abstractions;
using AwesomeAssertions;
using Domain.Rates;
using NodaTime;

public sealed class NodeRateOverrideTests
{
	private static readonly JobNodeId NodeId = new(1);

	[Fact]
	public void An_effective_end_at_or_before_the_effective_start_throws()
	{
		var start = Instant.FromUtc(2024, 1, 1, 0, 0);

		var act = () => new NodeRateOverride(NodeId, new(50m), start, start);

		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void An_instant_before_the_effective_start_is_not_effective()
	{
		var start = Instant.FromUtc(2024, 1, 1, 0, 0);
		var over = new NodeRateOverride(NodeId, new(50m), start, null);

		over.IsEffectiveAt(start - Duration.FromSeconds(1)).Should().BeFalse();
	}

	[Fact]
	public void An_instant_on_or_after_the_start_and_before_the_exclusive_end_is_effective()
	{
		var start = Instant.FromUtc(2024, 1, 1, 0, 0);
		var end = Instant.FromUtc(2024, 2, 1, 0, 0);
		var over = new NodeRateOverride(NodeId, new(50m), start, end);

		over.IsEffectiveAt(start).Should().BeTrue();
		over.IsEffectiveAt(end - Duration.FromSeconds(1)).Should().BeTrue();
		over.IsEffectiveAt(end).Should().BeFalse();
	}

	[Fact]
	public void With_no_effective_end_every_instant_from_the_start_onward_is_effective()
	{
		var start = Instant.FromUtc(2024, 1, 1, 0, 0);
		var over = new NodeRateOverride(NodeId, new(50m), start, null);

		over.IsEffectiveAt(Instant.FromUtc(2099, 1, 1, 0, 0)).Should().BeTrue();
	}
}
