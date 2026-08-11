namespace JobTrack.Domain.Schedules;

using Intervals;
using NodaTime;

/// <summary>
///     Expands one effective-dated <see cref="ScheduleVersion" />'s recurring civil-time weekly
///     intervals into concrete <see cref="Instant" />-based <see cref="WorkInterval" />s (spec §7.2 step
///     5, §8.1/§8.2), resolving every boundary through <see cref="CivilTimeResolver" /> so DST gaps and
///     folds are handled consistently, and honouring the version's effective date range. Only the
///     requesting date and zone determine which weekly-interval occurrences exist; results outside the
///     requested bounds are clipped, and the version's own occurrences are normalized first so no
///     instant is counted twice.
/// </summary>
public static class ScheduleExpander
{
	/// <summary>
	///     Expands <paramref name="schedule" />'s weekly intervals into <see cref="WorkInterval" />s
	///     overlapping <paramref name="bounds" />.
	/// </summary>
	public static IReadOnlyList<WorkInterval> Expand(ScheduleVersion schedule, WorkInterval bounds)
	{
		if (schedule.WeeklyIntervals.Count == 0) {
			return [];
		}

		var zone = schedule.Zone;

		// A weekly interval can cross midnight and a boundary can fall inside a DST gap or fold,
		// either of which can shift an occurrence's instants by up to roughly a day relative to
		// its nominal local date, so scan one day of slack on either side of the requested bounds.
		var firstDate = bounds.Start.InZone(zone).Date.PlusDays(-1);
		var lastDate = bounds.End.InZone(zone).Date.PlusDays(1);

		var expanded = new List<WorkInterval>();
		for (var date = firstDate; date <= lastDate; date = date.PlusDays(1)) {
			if (!schedule.IsEffectiveOn(date)) {
				continue;
			}

			foreach (var interval in schedule.WeeklyIntervals) {
				if (interval.Day != date.DayOfWeek) {
					continue;
				}

				var endDate = interval.CrossesMidnight ? date.PlusDays(1) : date;
				var start = CivilTimeResolver.ToInstant(date.At(interval.Start), zone);
				var end = CivilTimeResolver.ToInstant(endDate.At(interval.End), zone);

				if (end > start) {
					expanded.Add(new(start, end));
				}
			}
		}

		return IntervalAlgebra.Clip(IntervalAlgebra.Normalize(expanded), bounds);
	}
}
