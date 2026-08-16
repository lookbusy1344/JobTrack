namespace JobTrack.Persistence.Shared.Tests;

using AwesomeAssertions;
using NodaTime;

public sealed class MicrosecondTruncatingClockTests
{
	[Fact]
	public void A_reading_with_sub_microsecond_ticks_is_rounded_down_to_the_microsecond()
	{
		var withSubMicrosecondTicks = Instant.FromUtc(2026, 1, 1, 0, 0).PlusTicks(1_234_567);
		var clock = new MicrosecondTruncatingClock(new FixedClock(withSubMicrosecondTicks));

		clock.GetCurrentInstant().Should().Be(Instant.FromUtc(2026, 1, 1, 0, 0).PlusTicks(1_234_560));
	}

	[Fact]
	public void A_reading_already_aligned_to_the_microsecond_is_unchanged()
	{
		var alreadyAligned = Instant.FromUtc(2026, 1, 1, 0, 0).PlusTicks(1_230_000);
		var clock = new MicrosecondTruncatingClock(new FixedClock(alreadyAligned));

		clock.GetCurrentInstant().Should().Be(alreadyAligned);
	}

	private sealed class FixedClock(Instant now) : IClock
	{
		public Instant GetCurrentInstant() => now;
	}
}
