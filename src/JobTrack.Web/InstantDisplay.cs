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
	/// <summary>Whole days left at or above which a deadline's remainder is counted in weeks rather than days.</summary>
	private const int DeadlineWeeksDaysFloor = 14;

	/// <summary>Whole days left at or above which a deadline's remainder is counted in days rather than hours.</summary>
	private const int DeadlineDaysFloor = 2;

	/// <summary>Minutes left below which a deadline's remainder is counted in minutes rather than hours.</summary>
	private const int DeadlineMinutesCeiling = 120;

	/// <summary>Seconds left below which a deadline's remainder collapses to "now" rather than a unit count.</summary>
	private const int DeadlineNowSecondsCeiling = 60;

	private const int DaysPerWeek = 7;

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
	///     directions coarsen the same way: whole weeks at fourteen days or more, whole days from two days
	///     up to that, whole hours below that down to two hours, and whole minutes under two hours -- never
	///     silent, since a deadline within touching distance is exactly when the remainder matters most.
	///     Rounded to the nearest whole unit, not truncated: "almost 2 hours" reads as "(119 mins)", not
	///     "(118 mins)". Within a minute either side of the deadline, direction stops mattering and the
	///     field simply reads "(now)".
	///     <para>
	///         How overdue a job is only counts while it is still open (<paramref name="isOpen" />): a
	///         deadline missed by a job that has since ended is history, the same rule that keeps such a
	///         deadline out of <c>.jt-overdue</c> red. Time still to run is a plain fact either way.
	///     </para>
	/// </summary>
	internal static string FormatDeadline(Instant deadline, DateTimeZone zone, Instant now, bool isOpen)
	{
		var stamp = Format(deadline, zone);
		var gap = deadline >= now ? deadline - now : now - deadline;
		if (gap.TotalSeconds < DeadlineNowSecondsCeiling) {
			return $"{stamp} (now)";
		}

		if (deadline >= now) {
			return $"{stamp} ({Describe(gap)})";
		}

		return isOpen ? $"{stamp} ({Describe(gap)} overdue)" : stamp;
	}

	/// <summary>
	///     A non-negative gap of at least a minute as whole weeks, whole days, whole hours, or whole
	///     minutes, rounded to the nearest whole unit (a half rounds up). Direction is the caller's to
	///     name -- this only measures the distance. Which unit is used is decided by the raw day/minute
	///     count, not the rounded value, so a gap just under a switchover rounds within its current unit
	///     (e.g. "48 hrs", not "2 days") rather than across it.
	/// </summary>
	private static string Describe(Duration gap)
	{
		if (gap.Days >= DeadlineWeeksDaysFloor) {
			var roundedWeeks = (int)Math.Round(gap.TotalDays / DaysPerWeek, MidpointRounding.AwayFromZero);
			return $"{roundedWeeks} weeks";
		}

		if (gap.Days >= DeadlineDaysFloor) {
			var roundedDays = (int)Math.Round(gap.TotalDays, MidpointRounding.AwayFromZero);
			return $"{roundedDays} days";
		}

		if (gap.TotalMinutes < DeadlineMinutesCeiling) {
			var roundedMinutes = (int)Math.Round(gap.TotalMinutes, MidpointRounding.AwayFromZero);
			return $"{roundedMinutes} {(roundedMinutes == 1 ? "min" : "mins")}";
		}

		var roundedHours = (int)Math.Round(gap.TotalHours, MidpointRounding.AwayFromZero);
		return $"{roundedHours} {(roundedHours == 1 ? "hr" : "hrs")}";
	}

	/// <summary>
	///     Whether a "start by"/"finish by" deadline (<c>NeededStart</c>/<c>NeededFinish</c>) has already
	///     passed -- for colouring the rendered deadline red (<c>.jt-overdue</c>) wherever one is shown.
	///     Not past at the exact instant of <paramref name="now" />, only strictly before it.
	/// </summary>
	internal static bool IsPast(Instant instant, Instant now) => instant < now;
}
