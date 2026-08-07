namespace JobTrack.Domain.Authorization;

/// <summary>The independent facts <see cref="RequesterAccessPolicy.CanSubmit" /> evaluates.</summary>
public sealed record RequesterSubmissionFacts
{
	/// <summary>Whether the target holding area currently accepts requests.</summary>
	public required bool IsHoldingAreaActive { get; init; }

	/// <summary>Whether the actor is eligible for the holding area (department routing or global eligibility, ADR 0033 §3).</summary>
	public required bool ActorIsEligibleForHoldingArea { get; init; }
}
