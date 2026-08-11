namespace JobTrack.Abstractions;

/// <summary>
///     Contextual label for a <c>job_node</c>, derived from parent and child structure rather than
///     stored as row state (ADR 0035). Numeric values are stable wire values, not reference-table ids.
/// </summary>
public enum NodeKind
{
	/// <summary>No structural classification has been assigned yet.</summary>
	None = 0,

	/// <summary>The sole node with no parent. Structural; cannot hold <c>leaf_work</c>.</summary>
	Root = 1,

	/// <summary>A node with one or more children and no <c>leaf_work</c>.</summary>
	Branch = 2,

	/// <summary>A node with no children and zero-or-one <c>leaf_work</c>.</summary>
	Leaf = 3,
}
