namespace JobTrack.Domain.Tests.Schedules;

using AwesomeAssertions;
using Domain.Schedules;
using NodaTime.TimeZones;

public sealed class ScheduleZoneIdTests
{
	[Fact]
	public void A_retired_tzdb_alias_resolves_to_its_canonical_zone()
	{
		var zone = ScheduleZoneId.Resolve("Asia/Calcutta");

		zone.Id.Should().Be("Asia/Kolkata");
	}

	[Fact]
	public void An_already_canonical_zone_id_resolves_unchanged()
	{
		var zone = ScheduleZoneId.Resolve("Europe/London");

		zone.Id.Should().Be("Europe/London");
	}

	[Fact]
	public void An_unrecognized_zone_id_throws()
	{
		var act = () => ScheduleZoneId.Resolve("Bogus/NotAZone");

		act.Should().Throw<DateTimeZoneNotFoundException>();
	}
}
