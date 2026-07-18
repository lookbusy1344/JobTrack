namespace JobTrack.Persistence.Shared.Entities;

using Abstractions;
using NodaTime;

/// <summary>
///     Persistence shape of the <c>user_schedule_interval</c> table (schema version 0009) — one
///     recurring civil-time weekly working interval owned by a <see cref="ScheduleVersionEntity" />.
///     <see cref="CrossesMidnight" /> is stored as the schema's own explicit author-intent flag, but is
///     always recomputable from <see cref="StartTime" />/<see cref="EndTime" /> the same way the domain
///     <c>WeeklyInterval.CrossesMidnight</c> derives it, so a read never needs this column.
/// </summary>
internal sealed class ScheduleIntervalEntity
{
	public long Id { get; set; }

	public required ScheduleVersionId ScheduleVersionId { get; set; }

	public IsoDayOfWeek DayOfWeek { get; set; }

	public LocalTime StartTime { get; set; }

	public LocalTime EndTime { get; set; }

	public bool CrossesMidnight { get; set; }
}
