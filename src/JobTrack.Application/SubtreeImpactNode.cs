namespace JobTrack.Application;

using Abstractions;

/// <summary>One node inside a <see cref="SubtreeImpactResult" /> (ADR 0061).</summary>
public sealed record SubtreeImpactNode
{
	/// <summary>The node's identifier.</summary>
	public required JobNodeId Id { get; init; }

	/// <summary>The node's parent; null only for the permanent root.</summary>
	public required JobNodeId? ParentId { get; init; }

	/// <summary>Levels below the measured subtree root; the root itself is 0.</summary>
	public required int Depth { get; init; }

	/// <summary>The node's description.</summary>
	public required string Description { get; init; }

	/// <summary>
	///     Contextual root/branch/leaf label, derived from real parent/child structure at read time
	///     rather than stored (ADR 0035) — the tree view uses it to pick each row's kind glyph.
	/// </summary>
	public required NodeKind Kind { get; init; }

	/// <summary>The leaf's recorded outcome, if it has <c>leaf_work</c> with one.</summary>
	public required Achievement? Achievement { get; init; }

	/// <summary><c>work_session</c> rows attached to this node.</summary>
	public required int WorkSessionCount { get; init; }

	/// <summary>Whether this node is already archived.</summary>
	public required bool IsArchived { get; init; }
}
