namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     One node of <see cref="IJobQueries.GetJobSubtreeAsync" />'s bounded Browse subtree (ADR 0039).
/// </summary>
public sealed record JobSubtreeNodeResult
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

	/// <summary>
	///     This node's own prerequisite readiness (spec §6, <see cref="Domain.Hierarchy.ReadinessCalculator" />):
	///     <see langword="false" /> when a prerequisite declared on it or on any ancestor is unsatisfied.
	///     Evaluated per row against the same definition <see cref="IJobQueries.GetReadinessAsync" /> uses,
	///     so a row and that node's own readiness pill can never disagree (ADR 0043 decision 1). A branch is
	///     <em>not</em> unready merely because a descendant of it is -- readiness aggregates over ancestors,
	///     never over descendants.
	/// </summary>
	public required bool IsReady { get; init; }

	/// <summary>Whether this node has an attached <c>leaf_work</c> row.</summary>
	public required bool HasLeafWork { get; init; }

	/// <summary>
	///     The achievement recorded on this node's <c>leaf_work</c> row, or <see langword="null" /> when
	///     no leaf work is attached — a branch, or a leaf nobody has attached work to yet. Distinct from
	///     <see cref="Abstractions.Achievement.Waiting" />, which is attached work nobody has started.
	/// </summary>
	public Achievement? Achievement { get; init; }

	/// <summary>
	///     Whether this node has children beyond what this fetch expanded -- either past the breadth cap
	///     (ADR 0039 decision 2) or the depth cap. A drill-in (re-root Browse here) shows the rest.
	/// </summary>
	public required bool HasUnexpandedChildren { get; init; }

	/// <summary>
	///     Whether this node itself matched the requested <c>OwnershipFilter</c>/<c>JobArchiveFilter</c>.
	///     A node can appear with this <see langword="false" /> when it is a structural ancestor of a
	///     matching descendant (ADR 0039 decision 5's pass-through pruning).
	/// </summary>
	public required bool MatchesFilter { get; init; }

	/// <summary>
	///     Ordinal pre-order position within this fetch, rebased to 0 at the requested subtree root (ADR
	///     0039 decision 3) -- never the raw, unexposed <c>lft</c>/<c>rgt</c> storage encoding, since
	///     there isn't one (the schema is adjacency-list; see the plan's §1 correction). A branch's span
	///     (<see cref="SubtreeLft" />, <see cref="SubtreeRgt" />) encloses every descendant fetched with it.
	/// </summary>
	public required int SubtreeLft { get; init; }

	/// <summary>Ordinal post-order position paired with <see cref="SubtreeLft" /> (ADR 0039 decision 3).</summary>
	public required int SubtreeRgt { get; init; }

	/// <summary>
	///     This node's penny-rounded, level-reconciled cost (ADR 0002) -- its own cost for a leaf, or the
	///     rolled-up subtotal of its whole subtree for a branch (ADR 0039 decision 4).
	///     <see
	///         langword="null" />
	///     when the actor may not view this subtree's cost (
	///     <see
	///         cref="Domain.Authorization.CostAccessPolicy" />
	///     , ADR 0040) -- omitted, not a request-wide
	///     denial, so the tree structure always renders.
	/// </summary>
	public Money? Cost { get; init; }
}
