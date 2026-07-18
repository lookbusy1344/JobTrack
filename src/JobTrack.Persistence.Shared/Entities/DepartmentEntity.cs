namespace JobTrack.Persistence.Shared.Entities;

using Abstractions;

/// <summary>Persistence shape of the <c>department</c> table (ADR 0033).</summary>
internal sealed class DepartmentEntity
{
	public required DepartmentId Id { get; set; }

	public required string Name { get; set; }

	public bool IsActive { get; set; } = true;

	public long RowVersion { get; set; } = 1;
}
