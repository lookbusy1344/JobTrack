namespace JobTrack.Domain.Authorization;

using Abstractions;

/// <summary>The independent facts <see cref="LeafReopenAndStartAccessPolicy.CanReopenAndStartFor" /> evaluates.</summary>
public sealed record LeafReopenAndStartFacts
{
	/// <summary>Whether the actor controls the leaf's node.</summary>
	public required bool ActorControlsNode { get; init; }

	/// <summary>Whether the actor recorded a previous session on this leaf.</summary>
	public required bool ActorParticipatedPreviously { get; init; }

	/// <summary>The actor requesting the reopen-and-start composite.</summary>
	public required AppUserId ActorUserId { get; init; }

	/// <summary>The worker the new session would be started for.</summary>
	public required AppUserId TargetWorkedByUserId { get; init; }
}
