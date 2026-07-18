namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     One row of <see cref="IJobQueries.GetJobChildrenAsync" /> or <see cref="IJobQueries.SearchJobNodesAsync" />
///     — lighter than <see cref="JobNodeResult" /> (no <c>WriteUp</c> or full dates), but still able to
///     carry an optional displayed cost for list UIs that surface many job rows at once.
/// </summary>
public sealed record JobNodeSummaryResult
{
	/// <summary>The node's <c>job_node</c> identifier.</summary>
	public required JobNodeId Id { get; init; }

	/// <summary>The parent node's identifier. Null only for the permanent root.</summary>
	public required JobNodeId? ParentId { get; init; }

	/// <summary>Contextual root/branch/leaf label derived from parent and child structure, not stored.</summary>
	public required NodeKind Kind { get; init; }

	/// <summary>The node's description.</summary>
	public required string Description { get; init; }

	/// <summary>The employee who directly owns this node and, for authorization, its subtree; <see langword="null" /> if unassigned (the pickup pool).</summary>
	public required AppUserId? OwnerUserId { get; init; }

	/// <summary>The node's priority.</summary>
	public required Priority Priority { get; init; }

	/// <summary>The node's penny-rounded displayed cost, or <see langword="null" /> when unavailable to the caller.</summary>
	public Money? Cost { get; init; }

	/// <summary>The instant this node was archived, if archived. Archival never implies deletion.</summary>
	public Instant? ArchivedAt { get; init; }

	/// <summary>Whether this node has at least one direct child.</summary>
	public required bool HasChildren { get; init; }

	/// <summary>Whether this node has an attached <c>leaf_work</c> row.</summary>
	public required bool HasLeafWork { get; init; }
}
