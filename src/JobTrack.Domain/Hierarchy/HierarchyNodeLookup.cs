namespace JobTrack.Domain.Hierarchy;

using Abstractions;

/// <summary>
///     Resolves nodes from a materialized hierarchy graph, throwing a stable domain invariant when a
///     referenced node is absent rather than a BCL <see cref="KeyNotFoundException" />.
/// </summary>
internal static class HierarchyNodeLookup
{
	internal static HierarchyNode GetRequired(IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById, JobNodeId nodeId) =>
		nodesById.TryGetValue(nodeId, out var node)
			? node
			: throw new InvariantViolationException(
				"hierarchy.missing-node",
				$"Job node {nodeId} is missing from the hierarchy inputs.");
}
