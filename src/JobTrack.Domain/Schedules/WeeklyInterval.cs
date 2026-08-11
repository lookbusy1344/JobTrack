namespace JobTrack.Domain.Schedules;

using NodaTime;

/// <summary>
///     One civil-time working interval on a day of the week (spec §8.2): defined in wall-clock time,
///     independent of any specific date, zone, or instant until expanded against a schedule version.
///     <see cref="End" /> at or before <see cref="Start" /> denotes an interval that crosses midnight
///     into the following day, per §8.2's "an interval may cross midnight."
/// </summary>
public readonly record struct WeeklyInterval
{
	/// <summary>Creates a <see cref="WeeklyInterval" /> value.</summary>
	/// <exception cref="ArgumentOutOfRangeException">
	///     <paramref name="day" /> is <see cref="IsoDayOfWeek.None" />, or <paramref name="end" /> equals
	///     <paramref name="start" />.
	/// </exception>
	/// <exception cref="ArgumentException"><paramref name="start" /> or <paramref name="end" /> carries a sub-second component.</exception>
	public WeeklyInterval(IsoDayOfWeek day, LocalTime start, LocalTime end)
	{
		if (day == IsoDayOfWeek.None) {
			throw new ArgumentOutOfRangeException(nameof(day), day, "A weekly interval must fall on a specific day.");
		}

		if (end == start) {
			throw new ArgumentOutOfRangeException(nameof(end), end, "A weekly interval's end must differ from its start.");
		}

		if (start.TickOfSecond != 0) {
			throw new ArgumentException(
				"A weekly interval's start must be whole-second (temporal hardening plan Gap D): SQLite's tick-of-day and " +
				"PostgreSQL's microsecond time agree at second resolution, but not below it.", nameof(start));
		}

		if (end.TickOfSecond != 0) {
			throw new ArgumentException(
				"A weekly interval's end must be whole-second (temporal hardening plan Gap D): SQLite's tick-of-day and " +
				"PostgreSQL's microsecond time agree at second resolution, but not below it.", nameof(end));
		}

		Day = day;
		Start = start;
		End = end;
	}

	/// <summary>The day of the week this interval recurs on.</summary>
	public IsoDayOfWeek Day { get; }

	/// <summary>The inclusive local start of day time.</summary>
	public LocalTime Start { get; }

	/// <summary>The exclusive local end time, on <see cref="Day" /> unless <see cref="CrossesMidnight" />.</summary>
	public LocalTime End { get; }

	/// <summary>Whether this interval's end falls on the day following <see cref="Day" />.</summary>
	public bool CrossesMidnight => End <= Start;
}
