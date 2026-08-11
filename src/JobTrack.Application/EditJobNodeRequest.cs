namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     Input to <see cref="IJobCommands.EditAsync" />. Full-replace semantics: every editable field is
///     supplied, matching the node's post-edit state exactly.
/// </summary>
public sealed record EditJobNodeRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The node being edited.</summary>
	public required JobNodeId NodeId { get; init; }

	/// <summary>The node's description.</summary>
	public required string Description { get; init; }

	/// <summary>Free-form supplementary detail.</summary>
	public string? WriteUp { get; init; }

	/// <summary>The employee who directly owns the node and, for authorization, its subtree; <see langword="null" /> to release it to the pool (unassigned).</summary>
	public required AppUserId? OwnerUserId { get; init; }

	/// <summary>The estimated effort, in hours.</summary>
	public decimal? ExpectedDurationHours { get; init; }

	/// <summary>The estimated cost.</summary>
	public Money? ExpectedCost { get; init; }

	/// <summary>The instant this node's work is needed to start.</summary>
	public Instant? NeededStart { get; init; }

	/// <summary>The instant this node's work is needed to finish.</summary>
	public Instant? NeededFinish { get; init; }

	/// <summary>The node's priority.</summary>
	public required Priority Priority { get; init; }

	/// <summary>The caller's expected current optimistic-concurrency version.</summary>
	public required long Version { get; init; }
}
