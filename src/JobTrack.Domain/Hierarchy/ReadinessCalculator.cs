namespace JobTrack.Domain.Hierarchy;

using Abstractions;

/// <summary>
///     Derives prerequisite readiness (spec §6): a node is ready only when every prerequisite
///     attached directly to it or to any of its ancestors is satisfied, where satisfaction means the
///     required job's derived achievement (§5.2, <see cref="AchievementCalculator" />) is
///     <see cref="Achievement.Success" />. A prerequisite declared on an ancestor is an effective gate
///     for the whole subtree beneath it, so this walks the checked node and every ancestor, reporting
///     the required job and the declaring node for each inherited or direct blocker.
/// </summary>
public static class ReadinessCalculator
{
	/// <summary>
	///     Evaluates readiness for <paramref name="nodeId" /> against every <paramref name="prerequisites" />
	///     edge declared on it or on one of its ancestors in <paramref name="nodesById" />.
	/// </summary>
	public static ReadinessResult IsReady(
		JobNodeId nodeId,
		IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById,
		IReadOnlyCollection<PrerequisiteEdge> prerequisites)
	{
		var edgesByDependent = prerequisites
			.GroupBy(edge => edge.DependentJobId)
			.ToDictionary(group => group.Key, group => group.ToList());
		var achievedCache = new Dictionary<JobNodeId, bool>();
		var blockers = new List<UnsatisfiedPrerequisite>();

		JobNodeId? currentId = nodeId;
		while (currentId is JobNodeId id) {
			if (edgesByDependent.TryGetValue(id, out var edges)) {
				foreach (var edge in edges) {
					if (!achievedCache.TryGetValue(edge.RequiredJobId, out var achieved)) {
						achieved = AchievementCalculator.IsAchieved(edge.RequiredJobId, nodesById);
						achievedCache[edge.RequiredJobId] = achieved;
					}

					if (!achieved) {
						blockers.Add(new(edge.RequiredJobId, id));
					}
				}
			}

			currentId = HierarchyNodeLookup.GetRequired(nodesById, id).ParentId;
		}

		return new(blockers.Count == 0, [.. blockers]);
	}
}
