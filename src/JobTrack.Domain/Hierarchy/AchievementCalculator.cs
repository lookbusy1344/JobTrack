namespace JobTrack.Domain.Hierarchy;

using Abstractions;

/// <summary>
///     Derives recursive achievement (spec §5.2): a leaf succeeds only when its <c>LeafWork</c> has
///     canonical <see cref="Achievement.Success" />; a branch or the root succeeds iff every direct
///     child succeeds, hence iff every leaf in its subtree succeeds.
/// </summary>
public static class AchievementCalculator
{
	/// <summary>
	///     Whether <paramref name="nodeId" /> is achieved, evaluated over <paramref name="nodesById" />.
	///     Uses an explicit post-order traversal rather than recursion so evaluation depth is not
	///     bounded by the call stack, matching the deep-tree scale this hierarchy is expected to hold.
	/// </summary>
	public static bool IsAchieved(JobNodeId nodeId, IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById)
	{
		var achieved = new Dictionary<JobNodeId, bool>();
		var pending = new Stack<(JobNodeId Id, bool ChildrenEvaluated)>();
		pending.Push((nodeId, false));

		while (pending.Count > 0) {
			var (id, childrenEvaluated) = pending.Pop();
			var node = HierarchyNodeLookup.GetRequired(nodesById, id);

			if (node.ChildIds.Count == 0) {
				achieved[id] = node.LeafAchievement == Achievement.Success;
				continue;
			}

			if (childrenEvaluated) {
				achieved[id] = node.ChildIds.All(childId => achieved[childId]);
				continue;
			}

			pending.Push((id, true));
			foreach (var childId in node.ChildIds) {
				pending.Push((childId, false));
			}
		}

		return achieved[nodeId];
	}
}
