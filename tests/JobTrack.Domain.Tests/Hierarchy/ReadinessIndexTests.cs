namespace JobTrack.Domain.Tests.Hierarchy;

using Abstractions;
using AwesomeAssertions;
using Domain.Hierarchy;

/// <summary>
///     Confirms <see cref="ReadinessIndex" /> (2026-09-18 remediation plan 2.9) agrees with
///     <see cref="ReadinessCalculator.IsReady" />'s per-call result for every node of a generated
///     hierarchy, over many random shapes -- the index changes how the edge grouping and achievement
///     cache are shared across calls, never the readiness result itself.
/// </summary>
public sealed class ReadinessIndexTests
{
	private const int GeneratedHierarchyCount = 200;
	private const int MaxNodesPerHierarchy = 24;

	[Fact]
	public void Index_and_per_call_results_agree_over_generated_hierarchies()
	{
		var random = new Random(20260918);

		for (var iteration = 0; iteration < GeneratedHierarchyCount; ++iteration) {
			var (nodesById, edges) = GenerateHierarchy(random);
			var index = ReadinessIndex.Build(nodesById, edges);

			foreach (var nodeId in nodesById.Keys) {
				var expected = ReadinessCalculator.IsReady(nodeId, nodesById, edges);
				var actual = index.IsReady(nodeId);

				actual.IsReady.Should().Be(expected.IsReady, $"iteration {iteration}, node {nodeId.Value}");
				actual.Blockers.Should().BeEquivalentTo(expected.Blockers, $"iteration {iteration}, node {nodeId.Value}");
			}
		}
	}

	private static (Dictionary<JobNodeId, HierarchyNode> NodesById, List<PrerequisiteEdge> Edges) GenerateHierarchy(Random random)
	{
		var nodeCount = random.Next(1, MaxNodesPerHierarchy);
		var ids = Enumerable.Range(1, nodeCount).Select(value => new JobNodeId(value)).ToArray();
		var parentIds = new JobNodeId?[nodeCount];
		var childIds = new List<JobNodeId>[nodeCount];
		for (var i = 0; i < nodeCount; ++i) {
			childIds[i] = [];
		}

		for (var i = 1; i < nodeCount; ++i) {
			var parentIndex = random.Next(0, i);
			parentIds[i] = ids[parentIndex];
			childIds[parentIndex].Add(ids[i]);
		}

		var achievements = Enum.GetValues<Achievement>();
		var nodesById = new Dictionary<JobNodeId, HierarchyNode>();
		for (var i = 0; i < nodeCount; ++i) {
			var isLeaf = childIds[i].Count == 0;
			Achievement? achievement = isLeaf ? achievements[random.Next(achievements.Length)] : null;
			nodesById[ids[i]] = new(ids[i], parentIds[i], [.. childIds[i]], achievement);
		}

		var edgeCount = random.Next(0, nodeCount);
		var edges = new List<PrerequisiteEdge>();
		for (var i = 0; i < edgeCount; ++i) {
			var requiredId = ids[random.Next(nodeCount)];
			var dependentId = ids[random.Next(nodeCount)];
			edges.Add(new(requiredId, dependentId));
		}

		return (nodesById, edges);
	}
}
