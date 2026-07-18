namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     Input to <see cref="IJobCommands.AddChildAsync" />. Node kind is not chosen at creation time —
///     a new child is labelled Root/Branch/Leaf from parent and child structure when read back.
/// </summary>
public sealed record CreateJobNodeRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The new node's parent. The permanent root is created only by installation bootstrap.</summary>
	public required JobNodeId ParentId { get; init; }

	/// <summary>The new node's description.</summary>
	public required string Description { get; init; }

	/// <summary>Free-form supplementary detail.</summary>
	public string? WriteUp { get; init; }

	/// <summary>
	///     The employee who directly owns the new node and, for authorization, its subtree; <see langword="null" /> to leave it unassigned (the pickup
	///     pool).
	/// </summary>
	public required AppUserId? OwnerUserId { get; init; }

	/// <summary>The estimated effort, in hours.</summary>
	public decimal? ExpectedDurationHours { get; init; }

	/// <summary>The estimated cost.</summary>
	public Money? ExpectedCost { get; init; }

	/// <summary>The instant this node's work is needed to start.</summary>
	public Instant? NeededStart { get; init; }

	/// <summary>The instant this node's work is needed to finish.</summary>
	public Instant? NeededFinish { get; init; }

	/// <summary>The new node's priority.</summary>
	public required Priority Priority { get; init; }
}
