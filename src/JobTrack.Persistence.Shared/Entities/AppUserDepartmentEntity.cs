namespace JobTrack.Persistence.Shared.Entities;

using Abstractions;

/// <summary>Persistence shape of the <c>app_user_department</c> table (ADR 0033).</summary>
internal sealed class AppUserDepartmentEntity
{
	public required AppUserId AppUserId { get; set; }

	public required DepartmentId DepartmentId { get; set; }

	public bool? IsPrimary { get; set; }
}
