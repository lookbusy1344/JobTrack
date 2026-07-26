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
				var total = 0m;
				foreach (var childId in node.ChildIds) {
					total += costs[childId].Amount;
				}

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

	/// <summary>
	///     Returns each <paramref name="rootIds" /> entry's own subtree total only — the exact value
	///     <c>Aggregate(rootId, ...)[rootId]</c> yields, without materializing the per-node costs of every
	///     subtree it walks through.
	///     <para>
	///         A caller pricing many candidate roots against one worker's leaf costs (a listing page's
	///         bulk cost enrichment) would otherwise run one full post-order traversal per root and
	///         discard all but one entry from each, which is
	///         <c>O(roots × subtree)</c> per worker. This instead walks upward from each costed leaf once,
	///         crediting every requested root on that leaf's ancestor chain, which is
	///         <c>O(costed leaves × depth)</c> regardless of how many roots are requested.
	///     </para>
	///     <para>
	///         A <paramref name="leafCosts" /> entry whose node is absent from
	///         <paramref name="nodesById" /> or is not childless there is ignored, exactly as
	///         <see cref="Aggregate" /> ignores it: a branch's cost is always derived from its children,
	///         never read from <paramref name="leafCosts" />. Every requested root present in
	///         <paramref name="nodesById" /> gets an entry, zero when no costed leaf sits beneath it.
	///     </para>
	/// </summary>
	public static IReadOnlyDictionary<JobNodeId, Money> SumSubtreeTotals(
		IReadOnlyCollection<JobNodeId> rootIds,
		IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById,
		IReadOnlyDictionary<JobNodeId, Money> leafCosts)
	{
		ArgumentNullException.ThrowIfNull(rootIds);
		ArgumentNullException.ThrowIfNull(nodesById);
		ArgumentNullException.ThrowIfNull(leafCosts);

		var totals = new Dictionary<JobNodeId, decimal>(rootIds.Count);
		foreach (var rootId in rootIds) {
			if (nodesById.ContainsKey(rootId)) {
				totals[rootId] = 0m;
			}
		}

		if (totals.Count == 0) {
			return new Dictionary<JobNodeId, Money>();
		}

		foreach (var (leafId, cost) in leafCosts) {
			if (!nodesById.TryGetValue(leafId, out var leaf) || leaf.ChildIds.Count > 0) {
				continue;
			}

			// Upward walk rather than recursion: the chain can be as deep as the hierarchy itself, and
			// the DB-enforced cycle-free invariant (schema version 0005) guarantees it terminates at the
			// root's null ParentId.
			JobNodeId? currentId = leafId;
			while (currentId is JobNodeId id) {
				if (totals.ContainsKey(id)) {
					totals[id] += cost.Amount;
				}

				currentId = HierarchyNodeLookup.GetRequired(nodesById, id).ParentId;
			}
		}

		return totals.ToDictionary(entry => entry.Key, entry => new Money(entry.Value));
	}
}
