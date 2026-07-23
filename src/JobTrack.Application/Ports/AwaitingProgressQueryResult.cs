namespace JobTrack.Application.Ports;

using Abstractions;
using Domain.Hierarchy;

/// <summary>
///     Result of <see cref="IAwaitingProgressQueryPort.GetAwaitingProgressInputsAsync" />: every fact
///     <see cref="AwaitingProgressCalculator" /> needs materialized ahead of time — the complete node
///     graph, each node's display/filter/sort facts, and every prerequisite edge.
/// </summary>
internal sealed record AwaitingProgressQueryResult
{
	/// <summary>Every node in the tree, keyed by identifier.</summary>
	public required EquatableDictionary<JobNodeId, HierarchyNode> NodesById { get; init; }

	/// <summary>Every node's display/filter/sort facts, keyed by identifier.</summary>
	public required EquatableDictionary<JobNodeId, AwaitingProgressNodeFacts> FactsById { get; init; }

	/// <summary>Every prerequisite edge in the tree.</summary>
	public required EquatableArray<PrerequisiteEdge> Prerequisites { get; init; }
}
