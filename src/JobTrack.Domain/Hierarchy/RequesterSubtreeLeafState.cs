namespace JobTrack.Domain.Hierarchy;

using Abstractions;

/// <summary>
///     The <see cref="Achievement" />, if any, of one childless (leaf) node in a request's subtree — the
///     same "childless node" unit the PostgreSQL <c>node_succeeded</c> function evaluates (schema version
///     0013). <see cref="LeafAchievement" /> is <see langword="null" /> for a childless node with no
///     <c>LeafWork</c> row yet (not yet actionable), distinct from an explicit <see cref="Achievement.Waiting" />.
/// </summary>
public readonly record struct RequesterSubtreeLeafState
{
	/// <summary>The childless node's <see cref="Achievement" />, or <see langword="null" /> if it has no <c>LeafWork</c> yet.</summary>
	public required Achievement? LeafAchievement { get; init; }
}
