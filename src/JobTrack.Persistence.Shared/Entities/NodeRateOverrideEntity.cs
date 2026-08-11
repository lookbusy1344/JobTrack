namespace JobTrack.Persistence.Shared.Entities;

using Abstractions;
using NodaTime;

/// <summary>
///     Persistence shape of the <c>node_rate_override</c> table (schema version 0011) — one
///     effective-dated hourly rate override for a particular node and worker.
/// </summary>
internal sealed class NodeRateOverrideEntity
{
	public required NodeRateOverrideId Id { get; set; }

	public required JobNodeId NodeId { get; set; }

	public required AppUserId UserId { get; set; }

	public Instant EffectiveStart { get; set; }

	public Instant? EffectiveEnd { get; set; }

	public HourlyRate Rate { get; set; }

	public Instant ChangedAt { get; set; }

	public long RowVersion { get; set; }
}
