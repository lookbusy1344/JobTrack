namespace JobTrack.Persistence.Shared.Entities;

using Abstractions;
using NodaTime;

/// <summary>
///     Persistence shape of the <c>user_schedule_version</c> table (schema version 0009) — one
///     effective-dated schedule version. <see cref="IanaTimeZone" /> is stored as the plain zone id
///     string the column holds; the port translates it to/from the domain <c>DateTimeZone</c> snapshot,
///     so no EF-level zone converter is needed.
/// </summary>
internal sealed class ScheduleVersionEntity
{
	public required ScheduleVersionId Id { get; set; }

	public required AppUserId UserId { get; set; }

	public LocalDate EffectiveStart { get; set; }

	public LocalDate? EffectiveEnd { get; set; }

	public required string IanaTimeZone { get; set; }

	public Instant ChangedAt { get; set; }

	public long RowVersion { get; set; }
}
