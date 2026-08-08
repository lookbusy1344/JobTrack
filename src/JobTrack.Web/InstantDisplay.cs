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
	/// <summary>Whole days left at or above which a deadline's remainder is counted in days rather than hours.</summary>
	private const int DeadlineDaysFloor = 2;

	// CreateWithInvariantCulture, matching MoneyDisplay: the runtime image runs in ICU-less
	// globalization-invariant mode (see Dockerfile), where a named culture's month names would throw.
	private static readonly LocalDateTimePattern Pattern = LocalDateTimePattern.CreateWithInvariantCulture("d MMM yyyy HH:mm");
	private static readonly LocalTimePattern CompactTimePattern = LocalTimePattern.CreateWithInvariantCulture("HH:mm");
	private static readonly LocalDatePattern CompactDatePattern = LocalDatePattern.CreateWithInvariantCulture("d MMM");

	internal static string Format(Instant instant, DateTimeZone zone) => Pattern.Format(instant.InZone(zone).LocalDateTime);

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
	///     A deadline as its own record-card field: the full local stamp followed by how far it is from
	///     now -- "10 Aug 2026 16:00 (2 days)" before, "1 Aug 2026 10:00 (7 days overdue)" after. Both
	///     directions coarsen the same way, whole days while two or more separate the deadline from now
	///     and whole hours below that, and both fall silent under an hour, where "(0 hours)" would say
	///     less than the stamp already does. Truncated, never rounded up: a deadline is not further away
	///     -- nor further behind -- than it is.
	///     <para>
	///         How overdue a job is only counts while it is still open (<paramref name="isOpen" />): a
	///         deadline missed by a job that has since ended is history, the same rule that keeps such a
	///         deadline out of <c>.jt-overdue</c> red. Time still to run is a plain fact either way.
	///     </para>
	/// </summary>
	internal static string FormatDeadline(Instant deadline, DateTimeZone zone, Instant now, bool isOpen)
	{
		var stamp = Format(deadline, zone);
		if (deadline >= now) {
			return Describe(deadline - now) is string left ? $"{stamp} ({left})" : stamp;
		}

		return isOpen && Describe(now - deadline) is string over ? $"{stamp} ({over} overdue)" : stamp;
	}

	/// <summary>
	///     A non-negative gap as whole days or whole hours, or <see langword="null" /> under an hour.
	///     Direction is the caller's to name -- this only measures the distance.
	/// </summary>
	private static string? Describe(Duration gap)
	{
		if (gap.Days >= DeadlineDaysFloor) {
			return $"{gap.Days} days";
		}

		// Duration.Hours is the hour component (0-23), so the day component carries the rest -- at most
		// one day here, since two or more took the branch above.
		var wholeHours = gap.Days * NodaConstants.HoursPerDay + gap.Hours;
		return wholeHours < 1 ? null : $"{wholeHours} {(wholeHours == 1 ? "hour" : "hours")}";
	}

	/// <summary>
	///     Whether a "start by"/"finish by" deadline (<c>NeededStart</c>/<c>NeededFinish</c>) has already
	///     passed -- for colouring the rendered deadline red (<c>.jt-overdue</c>) wherever one is shown.
	///     Not past at the exact instant of <paramref name="now" />, only strictly before it.
	/// </summary>
	internal static bool IsPast(Instant instant, Instant now) => instant < now;
}
