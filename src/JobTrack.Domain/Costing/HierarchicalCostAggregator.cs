namespace JobTrack.Domain.Costing;

using Abstractions;
using Hierarchy;

/// <summary>
///     Derives hierarchical actual cost (spec §10.4): a leaf's cost is the sum of its work-session
///     costs, or zero if it has none; a branch's cost is the sum of all descendant leaf costs; the root
///     cost is the sum of all work in the requested interval. Uses an explicit post-order traversal, as
///     <see cref="Hierarchy.AchievementCalculator" /> does, so depth is not bounded by the call stack.
/// </summary>
public static class HierarchicalCostAggregator
{
	/// <summary>
	///     Computes the cost of <paramref name="nodeId" /> and every node in its subtree, from each
	///     leaf's own exact cost in <paramref name="leafCosts" /> (absent entries cost zero).
	/// </summary>
	public static IReadOnlyDictionary<JobNodeId, Money> Aggregate(
		JobNodeId nodeId, IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById, IReadOnlyDictionary<JobNodeId, Money> leafCosts)
	{
		var costs = new Dictionary<JobNodeId, Money>();
		var pending = new Stack<(JobNodeId Id, bool ChildrenEvaluated)>();
		pending.Push((nodeId, false));

		while (pending.Count > 0) {
			var (id, childrenEvaluated) = pending.Pop();
			var node = HierarchyNodeLookup.GetRequired(nodesById, id);

			if (node.ChildIds.Count == 0) {
				costs[id] = leafCosts.GetValueOrDefault(id, new(0m));
				continue;
			}

			if (childrenEvaluated) {
				var total = node.ChildIds.Sum(childId => costs[childId].Amount);
				costs[id] = new(total);
				continue;
			}

			pending.Push((id, true));
			foreach (var childId in node.ChildIds) {
				pending.Push((childId, false));
			}
		}

		return costs;
	}
}
