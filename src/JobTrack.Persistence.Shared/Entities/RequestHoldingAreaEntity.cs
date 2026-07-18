namespace JobTrack.Persistence.Shared.Entities;

using Abstractions;

/// <summary>
///     Persistence shape of the <c>request_holding_area</c> table (ADR 0033): a configured
///     <c>job_node</c> parent that accepts requester-created children.
/// </summary>
internal sealed class RequestHoldingAreaEntity
{
	public required RequestHoldingAreaId Id { get; set; }

	public required JobNodeId JobNodeId { get; set; }

	public DepartmentId? DepartmentId { get; set; }

	public required string Name { get; set; }

	public required Priority DefaultPriority { get; set; }

	public AppUserId? DefaultOwnerUserId { get; set; }

	public bool IsActive { get; set; } = true;

	public long RowVersion { get; set; } = 1;
}
