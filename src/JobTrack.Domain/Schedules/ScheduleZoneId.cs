namespace JobTrack.Domain.Schedules;

using NodaTime;
using NodaTime.TimeZones;

/// <summary>
///     Resolves a caller-supplied IANA time zone id to its <b>canonical</b> <see cref="DateTimeZone" />
///     at the write boundary (temporal hardening plan Gap C). TZDB aliases (<c>Asia/Calcutta</c> to
///     <c>Asia/Kolkata</c>, <c>US/Eastern</c> to <c>America/New_York</c>) resolve to a working zone via
///     the plain <c>DateTimeZoneProviders.Tzdb</c> indexer, but the returned zone's own
///     <see
///         cref="DateTimeZone.Id" />
///     is the alias as typed, not the canonical id -- so two users in the same
///     real zone can persist different strings, and aliases are exactly the ids TZDB is most likely to
///     retire later (Gap B). An unrecognized id still throws
///     <see cref="DateTimeZoneNotFoundException" />, unchanged from the prior behaviour this replaces.
/// </summary>
public static class ScheduleZoneId
{
	/// <summary>Resolves <paramref name="ianaTimeZone" /> to its canonical <see cref="DateTimeZone" />.</summary>
	public static DateTimeZone Resolve(string ianaTimeZone)
	{
		var canonicalId = TzdbDateTimeZoneSource.Default.CanonicalIdMap.TryGetValue(ianaTimeZone, out var mapped)
			? mapped
			: ianaTimeZone;
		return DateTimeZoneProviders.Tzdb[canonicalId];
	}
}
