namespace JobTrack.Persistence.Shared.Entities;

using Abstractions;
using NodaTime;

/// <summary>
///     Persistence shape of the <c>leaf_work</c> table (schema version 0006) — the 1:1 achievement
///     record for a leaf <see cref="JobNodeEntity" />, keyed on the leaf's own <c>job_node_id</c>.
/// </summary>
internal sealed class LeafWorkEntity
{
	public required JobNodeId JobNodeId { get; set; }

	public Achievement Achievement { get; set; }

	public string? PartialCriteria { get; set; }

	public string? FullCriteria { get; set; }

	public Instant ChangedAt { get; set; }

	public long RowVersion { get; set; }
}
