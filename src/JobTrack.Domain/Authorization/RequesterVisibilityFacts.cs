namespace JobTrack.Domain.Authorization;

/// <summary>The independent facts <see cref="RequesterAccessPolicy.CanView" /> evaluates.</summary>
public sealed record RequesterVisibilityFacts
{
	/// <summary>Whether the actor is the request's own requester.</summary>
	public required bool ActorIsRequestOwner { get; init; }

	/// <summary>Whether department-scoped requester visibility is enabled for this request.</summary>
	public required bool IsDepartmentVisibilityEnabled { get; init; }

	/// <summary>Whether the actor shares the request's department.</summary>
	public required bool ActorSharesRequestDepartment { get; init; }

	/// <summary>Whether the actor controls the request's anchor node.</summary>
	public required bool ActorControlsAnchorNode { get; init; }
}
