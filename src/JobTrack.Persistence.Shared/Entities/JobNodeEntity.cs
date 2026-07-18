namespace JobTrack.Persistence.Shared.Entities;

using Abstractions;
using NodaTime;

/// <summary>
///     Persistence shape of the <c>job_node</c> table (schema version 0004) — the hierarchy table,
///     including the permanent root (<see cref="ParentId" /> <see langword="null" />). Move/re-parent
///     procedures, leaf/branch exclusivity, and achievement live in later schema slices, not here.
/// </summary>
internal sealed class JobNodeEntity
{
	public required JobNodeId Id { get; set; }

	public JobNodeId? ParentId { get; set; }

	public required string Description { get; set; }

	public string? WriteUp { get; set; }

	public required AppUserId PostedByUserId { get; set; }

	public AppUserId? OwnerUserId { get; set; }

	public decimal? ExpectedDurationHours { get; set; }

	public Money? ExpectedCost { get; set; }

	public Instant? NeededStart { get; set; }

	public Instant? NeededFinish { get; set; }

	public Priority Priority { get; set; }

	public Instant PostedAt { get; set; }

	public Instant? ArchivedAt { get; set; }

	public long RowVersion { get; set; }
}
