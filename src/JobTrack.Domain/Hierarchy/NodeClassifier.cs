namespace JobTrack.Domain.Hierarchy;

using Abstractions;

/// <summary>
///     Classifies one <see cref="HierarchyNode" /> as root, branch, or leaf and enforces the per-node
///     hierarchy invariants of spec §4.2 (items 4 and 7-10): a node cannot be its own parent, the root
///     cannot own <c>LeafWork</c>, and a node cannot have both children and <c>LeafWork</c>. Whole-tree
///     invariants — exactly one root, acyclicity, reachability (items 1-3, 5-6) — are enforced where
///     the full node set is available, not per node.
/// </summary>
public static class NodeClassifier
{
	/// <summary>Classifies <paramref name="node" />.</summary>
	/// <exception cref="InvariantViolationException">A per-node hierarchy invariant is violated.</exception>
	public static NodeKind Classify(HierarchyNode node)
	{
		if (node.ParentId == node.Id) {
			throw new InvariantViolationException("hierarchy.self-parent", $"Node {node.Id.Value} cannot be its own parent.");
		}

		if (node.ParentId is null) {
			return node.LeafAchievement is null
				? NodeKind.Root
				: throw new InvariantViolationException("hierarchy.root-has-leaf-work", $"Root node {node.Id.Value} cannot own LeafWork.");
		}

		if (node.ChildIds.Count > 0) {
			return node.LeafAchievement is null
				? NodeKind.Branch
				: throw new InvariantViolationException(
					"hierarchy.node-has-both-children-and-leaf-work",
					$"Node {node.Id.Value} cannot have both children and LeafWork.");
		}

		return NodeKind.Leaf;
	}
}
