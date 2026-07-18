namespace JobTrack.Persistence.Shared;

using Abstractions;
using NodaTime;

/// <summary>
///     Resolves a persisted IANA time zone id read back from storage. Unlike the throwing
///     <c>DateTimeZoneProviders.Tzdb[id]</c> indexer -- which is correct at the write boundary, where an
///     unrecognized id is the caller's mistake -- a read that hits a since-retired alias is the store's
///     data rotting under a TZDB upgrade, not a bad request, so it is surfaced as
///     <see cref="UnknownStoredTimeZoneException" /> (a domain fault) rather than the framework's
///     <see cref="NodaTime.TimeZones.DateTimeZoneNotFoundException" /> (temporal hardening plan Gap B).
/// </summary>
internal static class StoredTimeZoneResolver
{
	public static DateTimeZone Resolve(string ianaTimeZone, string rowDescription) =>
		DateTimeZoneProviders.Tzdb.GetZoneOrNull(ianaTimeZone)
		?? throw new UnknownStoredTimeZoneException(
			$"{rowDescription} stores time zone '{ianaTimeZone}', which the current TZDB no longer recognizes.");
}
