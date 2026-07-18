namespace JobTrack.Domain.Hierarchy;

/// <summary>Which shape an <see cref="OwnershipFilter" /> represents.</summary>
public enum OwnershipFilterKind
{
	/// <summary>Every node matches, regardless of owner.</summary>
	All = 0,

	/// <summary>Only nodes with no direct owner (the pickup pool).</summary>
	Unassigned = 1,

	/// <summary>Only nodes directly owned by a specific user.</summary>
	OwnedBy = 2,
}
