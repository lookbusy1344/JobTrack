namespace JobTrack.Persistence.Shared.Entities;

using NodaTime;

/// <summary>
///     Persistence shape of the singleton, append-only <c>initialised_marker</c> table (schema
///     version 0003, ADR 0015). No concurrency token — the row is inserted at most once and never
///     updated or deleted (enforced by database triggers, not EF).
/// </summary>
internal sealed class InitialisedMarkerEntity
{
	public short Id { get; set; }

	public Instant InitialisedAt { get; set; }
}
