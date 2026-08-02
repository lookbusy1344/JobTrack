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
	private static readonly LocalDatePattern DatePattern = LocalDatePattern.CreateWithInvariantCulture("d MMM yyyy");

	internal static string Format(Instant instant, DateTimeZone zone) => Pattern.Format(instant.InZone(zone).LocalDateTime);

	/// <summary>
	///     Just the calendar date (<c>d MMM yyyy</c>), no time-of-day -- for a deadline shown inline
	///     beside another field (e.g. "Priority High (deadline 26 Jul 2026)"), where the full timestamp
	///     would read as more precision than a deadline actually carries.
	/// </summary>
	internal static string FormatDate(Instant instant, DateTimeZone zone) => DatePattern.Format(instant.InZone(zone).Date);

	/// <summary>
	///     A narrower rendering for "Active since" status pills and attention-list deadline columns
	///     (Browse's subtree Deadline, AwaitingProgress's Due), where the full date-and-time stamp reads
	///     too wide -- just the time-of-day (<c>HH:mm</c>) when <paramref name="instant" /> falls on the
	///     viewer's current calendar day, otherwise just the date (<c>d MMM</c>). A due-today deadline is
	///     the one case where the hour actually matters; every other day only the date does.
	/// </summary>
	internal static string FormatCompact(Instant instant, DateTimeZone zone, Instant now)
	{
		var local = instant.InZone(zone).LocalDateTime;
		var today = now.InZone(zone).Date;
		return local.Date == today ? CompactTimePattern.Format(local.TimeOfDay) : CompactDatePattern.Format(local.Date);
	}

	/// <summary>
	///     Whether a "start by"/"finish by" deadline (<c>NeededStart</c>/<c>NeededFinish</c>) has already
	///     passed -- for colouring the rendered deadline red (<c>.jt-overdue</c>) wherever one is shown.
	///     Not past at the exact instant of <paramref name="now" />, only strictly before it.
	/// </summary>
	internal static bool IsPast(Instant instant, Instant now) => instant < now;
}
