namespace JobTrack.Domain.Tests.Schedules;

using AwesomeAssertions;
using Domain.Schedules;
using NodaTime;

public sealed class CivilTimeResolverTests
{
	private static readonly DateTimeZone NewYork = DateTimeZoneProviders.Tzdb["America/New_York"];

	[Fact]
	public void A_local_time_skipped_by_a_spring_forward_gap_shifts_forward_by_the_gap_length()
	{
		// 2024-03-10: America/New_York springs forward at 02:00 local (EST, UTC-5) to 03:00 local (EDT, UTC-4).
		var skipped = new LocalDateTime(2024, 3, 10, 2, 30, 0);

		var resolved = CivilTimeResolver.ToInstant(skipped, NewYork);

		resolved.InZone(NewYork).LocalDateTime.Should().Be(new(2024, 3, 10, 3, 30, 0));
	}

	[Fact]
	public void A_local_time_repeated_by_an_autumn_fold_resolves_to_the_earlier_instant()
	{
		// 2024-11-03: America/New_York falls back at 02:00 local (EDT, UTC-4) to 01:00 local (EST, UTC-5),
		// so 01:00-02:00 local occurs twice.
		var ambiguous = new LocalDateTime(2024, 11, 3, 1, 30, 0);
		var mapping = NewYork.MapLocal(ambiguous);
		mapping.Count.Should().Be(2, "the chosen local time must actually be ambiguous for this test to prove anything");

		var resolved = CivilTimeResolver.ToInstant(ambiguous, NewYork);
		var earlyInstant = new OffsetDateTime(ambiguous, mapping.EarlyInterval.WallOffset).ToInstant();

		resolved.Should().Be(earlyInstant);
	}

	[Fact]
	public void An_unambiguous_local_time_resolves_to_its_single_instant()
	{
		var unambiguous = new LocalDateTime(2024, 6, 15, 9, 0, 0);

		var resolved = CivilTimeResolver.ToInstant(unambiguous, NewYork);

		resolved.Should().Be(unambiguous.InZoneStrictly(NewYork).ToInstant());
	}
}
