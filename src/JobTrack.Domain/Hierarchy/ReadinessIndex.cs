namespace JobTrack.Domain.Hierarchy;

using Abstractions;

/// <summary>
///     A prerequisite edge set and node map, indexed once (<see cref="Build" />) so
///     <see cref="IsReady" /> can be called per row of a page without re-grouping the edges or
///     re-deriving each required job's achievement on every call (2026-09-18 remediation plan 2.9): a
///     page of <em>n</em> rows over <em>e</em> edges previously did <em>n × e</em> grouping work through
///     <see cref="ReadinessCalculator.IsReady" />, re-deriving the same required job's achievement once
///     per row that shares it. Build once per request; the achievement cache is shared across every
///     <see cref="IsReady" /> call on the same index.
/// </summary>
public sealed class ReadinessIndex
{
	private readonly Dictionary<JobNodeId, bool> _achievedCache = [];
	private readonly IReadOnlyDictionary<JobNodeId, List<PrerequisiteEdge>> _edgesByDependent;
	private readonly IReadOnlyDictionary<JobNodeId, HierarchyNode> _nodesById;

	private ReadinessIndex(
		IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById,
		IReadOnlyDictionary<JobNodeId, List<PrerequisiteEdge>> edgesByDependent)
	{
		_nodesById = nodesById;
		_edgesByDependent = edgesByDependent;
	}

	/// <summary>Groups <paramref name="prerequisites" /> by dependent job once, for repeated <see cref="IsReady" /> calls.</summary>
	public static ReadinessIndex Build(
		IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById, IReadOnlyCollection<PrerequisiteEdge> prerequisites) =>
		new(
			nodesById,
			prerequisites.GroupBy(edge => edge.DependentJobId).ToDictionary(group => group.Key, group => group.ToList()));

	/// <summary>Evaluates readiness for <paramref name="nodeId" />, the same result <see cref="ReadinessCalculator.IsReady" /> would give.</summary>
	public ReadinessResult IsReady(JobNodeId nodeId)
	{
		var blockers = new List<UnsatisfiedPrerequisite>();

		JobNodeId? currentId = nodeId;
		while (currentId is JobNodeId id) {
			if (_edgesByDependent.TryGetValue(id, out var edges)) {
				foreach (var edge in edges) {
					if (!_achievedCache.TryGetValue(edge.RequiredJobId, out var achieved)) {
						achieved = AchievementCalculator.IsAchieved(edge.RequiredJobId, _nodesById);
						_achievedCache[edge.RequiredJobId] = achieved;
					}

					if (!achieved) {
						blockers.Add(new(edge.RequiredJobId, id));
					}
				}
			}

			currentId = HierarchyNodeLookup.GetRequired(_nodesById, id).ParentId;
		}

		return new(blockers.Count == 0, [.. blockers]);
	}
}
