namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     One row of <see cref="Ports.IJobBrowseQueryPort.GetSubtreeAsync" /> -- a node inside a bounded
///     Browse subtree fetch (ADR 0039). Persistence-internal: Stage 3 maps this onto the public
///     <c>JobSubtreeResult</c> shape (interval ordinals, cost roll-up) rather than exposing it directly.
/// </summary>
internal sealed record JobNodeSubtreeRow
{
	/// <summary>The node's <c>job_node</c> identifier.</summary>
	public required JobNodeId Id { get; init; }

	/// <summary>The parent node's identifier. Null only for the permanent root.</summary>
	public required JobNodeId? ParentId { get; init; }

	/// <summary>Contextual root/branch/leaf label derived from parent and child structure, not stored.</summary>
	public required NodeKind Kind { get; init; }

	/// <summary>Depth below the requested subtree root; the root itself is 0.</summary>
	public required int Depth { get; init; }

	/// <summary>The node's description.</summary>
	public required string Description { get; init; }

	/// <summary>The employee who directly owns this node; <see langword="null" /> if unassigned.</summary>
	public required AppUserId? OwnerUserId { get; init; }

	/// <summary>The node's priority.</summary>
	public required Priority Priority { get; init; }

	/// <summary>The instant this node was archived, if archived.</summary>
	public Instant? ArchivedAt { get; init; }

	/// <summary>Whether this node has at least one direct child.</summary>
	public required bool HasChildren { get; init; }

	/// <summary>Whether this node has an attached <c>leaf_work</c> row.</summary>
	public required bool HasLeafWork { get; init; }

	/// <summary>
	///     The achievement recorded on this node's <c>leaf_work</c> row, or <see langword="null" /> when
	///     no leaf work is attached — a branch, or a leaf nobody has attached work to yet. Distinct from
	///     <see cref="Abstractions.Achievement.Waiting" />, which is attached work nobody has started.
	/// </summary>
	public Achievement? Achievement { get; init; }

	/// <summary>
	///     Whether this node has children beyond what the fetch expanded -- either because it fell past
	///     the breadth cap (ADR 0039 decision 2) or because the depth cap stopped recursion at this node.
	///     A drill-in (re-root Browse here) shows the rest.
	/// </summary>
	public required bool HasUnexpandedChildren { get; init; }

	/// <summary>
	///     Whether this node itself matched the requested <c>OwnershipFilter</c>/<c>JobArchiveFilter</c>.
	///     A node can appear in the result with this <see langword="false" /> when it is a structural
	///     ancestor of a matching descendant (ADR 0039 decision 5's pass-through pruning) -- the caller
	///     uses this flag to highlight matches without hiding the connecting structure.
	/// </summary>
	public required bool MatchesFilter { get; init; }
}
