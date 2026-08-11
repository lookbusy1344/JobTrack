namespace JobTrack.Domain.Costing;

using Abstractions;
using Hierarchy;

/// <summary>Rolls exact allocated work duration from leaves through a job-node hierarchy.</summary>
public static class HierarchicalAllocatedDurationAggregator
{
	/// <summary>
	///     Computes the exact allocated duration of <paramref name="nodeId" /> and every node in its
	///     subtree from each leaf's own duration.
	/// </summary>
	public static IReadOnlyDictionary<JobNodeId, AllocatedDuration> Aggregate(
		JobNodeId nodeId,
		IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById,
		IReadOnlyDictionary<JobNodeId, AllocatedDuration> leafDurations)
	{
		ArgumentNullException.ThrowIfNull(nodesById);
		ArgumentNullException.ThrowIfNull(leafDurations);

		var durations = new Dictionary<JobNodeId, AllocatedDuration>();
		var pending = new Stack<(JobNodeId Id, bool ChildrenEvaluated)>();
		pending.Push((nodeId, false));

		while (pending.Count > 0) {
			var (id, childrenEvaluated) = pending.Pop();
			var node = HierarchyNodeLookup.GetRequired(nodesById, id);

			if (node.ChildIds.Count == 0) {
				durations[id] = leafDurations.GetValueOrDefault(id, AllocatedDuration.Zero);
				continue;
			}

			if (childrenEvaluated) {
				var total = AllocatedDuration.Zero;
				foreach (var childId in node.ChildIds) {
					total = total.Add(durations[childId]);
				}

				durations[id] = total;
				continue;
			}

			pending.Push((id, true));
			foreach (var childId in node.ChildIds) {
				pending.Push((childId, false));
			}
		}

		return durations;
	}

	/// <summary>Returns only each requested root's exact allocated-duration subtree total.</summary>
	public static IReadOnlyDictionary<JobNodeId, AllocatedDuration> SumSubtreeTotals(
		IReadOnlyCollection<JobNodeId> rootIds,
		IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById,
		IReadOnlyDictionary<JobNodeId, AllocatedDuration> leafDurations)
	{
		ArgumentNullException.ThrowIfNull(rootIds);
		ArgumentNullException.ThrowIfNull(nodesById);
		ArgumentNullException.ThrowIfNull(leafDurations);

		var totals = new Dictionary<JobNodeId, AllocatedDuration>(rootIds.Count);
		foreach (var rootId in rootIds) {
			if (nodesById.ContainsKey(rootId)) {
				totals[rootId] = AllocatedDuration.Zero;
			}
		}

		foreach (var (leafId, duration) in leafDurations) {
			if (!nodesById.TryGetValue(leafId, out var leaf) || leaf.ChildIds.Count > 0) {
				continue;
			}

			JobNodeId? currentId = leafId;
			while (currentId is JobNodeId id) {
				if (totals.TryGetValue(id, out var total)) {
					totals[id] = total.Add(duration);
				}

				currentId = HierarchyNodeLookup.GetRequired(nodesById, id).ParentId;
			}
		}

		return totals;
	}
}
