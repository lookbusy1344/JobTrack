namespace JobTrack.Web;

using NodaTime;
using NodaTime.Text;

/// <summary>
///     Shared formatting for user-facing timestamps across the web host. Every <see cref="Instant" />
///     is shown converted into the viewing employee's own <see cref="DateTimeZone" />
///     (<see cref="IViewerTimeZoneResolver" />), never raw UTC -- see the
///     "everywhere a time is entered or shown" review that motivated this type.
/// </summary>
internal static class InstantDisplay
{
	// CreateWithInvariantCulture, matching MoneyDisplay: the runtime image runs in ICU-less
	// globalization-invariant mode (see Dockerfile), where a named culture's month names would throw.
	private static readonly LocalDateTimePattern Pattern = LocalDateTimePattern.CreateWithInvariantCulture("d MMM yyyy HH:mm");
	private static readonly LocalTimePattern CompactTimePattern = LocalTimePattern.CreateWithInvariantCulture("HH:mm");
	private static readonly LocalDatePattern CompactDatePattern = LocalDatePattern.CreateWithInvariantCulture("d MMM");

	internal static string Format(Instant instant, DateTimeZone zone) => Pattern.Format(instant.InZone(zone).LocalDateTime);

	/// <summary>
	///     A narrower rendering for "Active since" status pills, where the full date-and-time stamp
	///     reads too wide -- just the time-of-day (<c>HH:mm</c>) when <paramref name="instant" /> falls
	///     on the viewer's current calendar day, otherwise just the date (<c>d MMM</c>). The pill's own
	///     colour already carries "in progress"; the timestamp only needs to say when.
	/// </summary>
	internal static string FormatCompact(Instant instant, DateTimeZone zone)
	{
		var local = instant.InZone(zone).LocalDateTime;
		var today = SystemClock.Instance.GetCurrentInstant().InZone(zone).Date;
		return local.Date == today ? CompactTimePattern.Format(local.TimeOfDay) : CompactDatePattern.Format(local.Date);
	}
}
