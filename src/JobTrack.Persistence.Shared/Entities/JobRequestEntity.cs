namespace JobTrack.Persistence.Shared.Entities;

using Abstractions;
using NodaTime;

/// <summary>
///     Persistence shape of the <c>job_request</c> table (ADR 0033): anchors requester
///     ownership/visibility to a specific <c>job_node</c> independently of
///     <see cref="JobNodeEntity.OwnerUserId" /> (technical ownership) and independently of later moves
///     or decomposition.
/// </summary>
internal sealed class JobRequestEntity
{
	public required JobNodeId JobNodeId { get; set; }

	public required AppUserId RequesterUserId { get; set; }

	public required RequestHoldingAreaId HoldingAreaId { get; set; }

	public string? RequesterReference { get; set; }

	public Instant SubmittedAt { get; set; }

	public Instant? ClosedToRequesterAt { get; set; }

	public Instant? AcknowledgedAt { get; set; }

	public AppUserId? AcknowledgedByUserId { get; set; }

	public long RowVersion { get; set; } = 1;
}
