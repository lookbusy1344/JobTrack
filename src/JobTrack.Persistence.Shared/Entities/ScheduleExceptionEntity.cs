namespace JobTrack.Persistence.Shared.Entities;

using Abstractions;
using NodaTime;

/// <summary>
///     Persistence shape of the <c>user_schedule_exception</c> table (schema version 0010) — one dated
///     additive or subtractive exception, only ever priced for <see cref="ScheduleExceptionEffectId" />
///     <c>1</c> (<c>AddWorkingTime</c>).
/// </summary>
internal sealed class ScheduleExceptionEntity
{
	public required ScheduleExceptionId Id { get; set; }

	public required AppUserId UserId { get; set; }

	public Instant StartedAt { get; set; }

	public Instant FinishedAt { get; set; }

	public short ScheduleExceptionEffectId { get; set; }

	public HourlyRate? RateOverride { get; set; }

	public required string Reason { get; set; }

	public required AppUserId CreatedBy { get; set; }

	public Instant ChangedAt { get; set; }

	public long RowVersion { get; set; }
}
