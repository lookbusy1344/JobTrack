namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>Result of every job-node structural command in <see cref="IJobCommands" /> except <c>Delete</c>.</summary>
public sealed record JobNodeResult
{
	/// <summary>The node's <c>job_node</c> identifier.</summary>
	public required JobNodeId Id { get; init; }

	/// <summary>The parent node's identifier. Null only for the permanent root.</summary>
	public required JobNodeId? ParentId { get; init; }

	/// <summary>Contextual root/branch/leaf label derived from parent and child structure, not stored.</summary>
	public required NodeKind Kind { get; init; }

	/// <summary>Whether this node has at least one direct child.</summary>
	public required bool HasChildren { get; init; }

	/// <summary>Whether this node has an attached <c>leaf_work</c> row.</summary>
	public required bool HasLeafWork { get; init; }

	/// <summary>The node's description.</summary>
	public required string Description { get; init; }

	/// <summary>Free-form supplementary detail.</summary>
	public string? WriteUp { get; init; }

	/// <summary>The employee who posted this node.</summary>
	public required AppUserId PostedByUserId { get; init; }

	/// <summary>The employee who directly owns this node and, for authorization, its subtree; <see langword="null" /> if unassigned (the pickup pool).</summary>
	public required AppUserId? OwnerUserId { get; init; }

	/// <summary>The estimated effort, in hours, if supplied.</summary>
	public decimal? ExpectedDurationHours { get; init; }

	/// <summary>The estimated cost, if supplied.</summary>
	public Money? ExpectedCost { get; init; }

	/// <summary>The instant this node's work is needed to start, if supplied.</summary>
	public Instant? NeededStart { get; init; }

	/// <summary>The instant this node's work is needed to finish, if supplied.</summary>
	public Instant? NeededFinish { get; init; }

	/// <summary>The node's priority.</summary>
	public required Priority Priority { get; init; }

	/// <summary>The instant this node was posted.</summary>
	public required Instant PostedAt { get; init; }

	/// <summary>The instant this node was archived, if archived. Archival never implies deletion.</summary>
	public Instant? ArchivedAt { get; init; }

	/// <summary>The node's optimistic-concurrency version.</summary>
	public required long Version { get; init; }
}
