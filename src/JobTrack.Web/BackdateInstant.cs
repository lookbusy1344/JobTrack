namespace JobTrack.Web;

using Domain.Schedules;
using NodaTime;
using NodaTime.Text;

/// <summary>
///     The <c>datetime-local</c> round trip: a bare wall-clock string with no offset, read as (and
///     written back as) that wall-clock time in the viewing employee's own <see cref="DateTimeZone" />
///     (<see cref="IViewerTimeZoneResolver" />), resolved through <see cref="CivilTimeResolver" /> so a
///     DST-edge backdate follows the exact same policy (ADR 0008) as everywhere else in the app that
///     maps local time to an <see cref="Instant" />.
/// </summary>
internal static class BackdateInstant
{
	// The exact shape the HTML5 datetime-local input type sends/accepts: no seconds, no offset.
	private static readonly LocalDateTimePattern Pattern = LocalDateTimePattern.CreateWithInvariantCulture("yyyy-MM-dd'T'HH:mm");

	/// <summary>
	///     Parses a <c>datetime-local</c> value as wall-clock time in <paramref name="zone" />.
	///     <see langword="false" /> for <see langword="null" />, empty, or unparsable input -- callers
	///     decide what that means for their field (an optional backdate left blank vs. a required
	///     field that must be re-validated).
	/// </summary>
	internal static bool TryParse(string? rawDateTimeLocal, DateTimeZone zone, out Instant instant)
	{
		if (string.IsNullOrEmpty(rawDateTimeLocal)) {
			instant = default;
			return false;
		}

		var result = Pattern.Parse(rawDateTimeLocal);
		if (!result.Success) {
			instant = default;
			return false;
		}

		instant = CivilTimeResolver.ToInstant(result.Value, zone);
		return true;
	}

	/// <summary>The inverse of <see cref="TryParse" />: pre-fills a <c>datetime-local</c> input from a stored <see cref="Instant" />.</summary>
	internal static string ToDateTimeLocalValue(Instant instant, DateTimeZone zone) => Pattern.Format(instant.InZone(zone).LocalDateTime);
}
