namespace JobTrack.Application.Ports;

using Abstractions;
using Domain.Hierarchy;

/// <summary>
///     Result of <see cref="IAwaitingProgressQueryPort.GetAwaitingProgressInputsAsync" />: every fact
///     <see cref="AwaitingProgressCalculator" /> needs materialized ahead of time, narrowed to
///     currently-unfinished leaves plus the ancestor/required-job facts readiness needs (2026-07-24
///     code-review-scalability-remediation-plan §2.2 step 4).
/// </summary>
internal sealed record AwaitingProgressQueryResult
{
	/// <summary>Every unfinished leaf, its ancestors, and any required job referenced by an in-scope prerequisite — not every node in the tree.</summary>
	public required EquatableDictionary<JobNodeId, HierarchyNode> NodesById { get; init; }

	/// <summary>Every node's display/filter/sort facts, keyed by identifier.</summary>
	public required EquatableDictionary<JobNodeId, AwaitingProgressNodeFacts> FactsById { get; init; }

	/// <summary>Every prerequisite edge in the tree.</summary>
	public required EquatableArray<PrerequisiteEdge> Prerequisites { get; init; }
}
