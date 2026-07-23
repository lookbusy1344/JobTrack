namespace JobTrack.Application.Ports;

using Abstractions;
using Domain.Hierarchy;

/// <summary>
///     Result of <see cref="IReadinessQueryPort.GetReadinessInputsAsync" />: every fact
///     <see cref="JobTrack.Domain.Hierarchy.ReadinessCalculator" /> needs materialized ahead of time —
///     the target node and its ancestors, every prerequisite declared on any of them, and the complete
///     subtree of every required job (achievement derivation is recursive, so a branch's own
///     achievement depends on its whole subtree, not just the node itself).
/// </summary>
internal sealed record ReadinessQueryResult
{
	/// <summary>Every node needed to evaluate readiness and derive achievement, keyed by identifier.</summary>
	public required EquatableDictionary<JobNodeId, HierarchyNode> NodesById { get; init; }

	/// <summary>Every prerequisite edge declared on the target node or any of its ancestors.</summary>
	public required EquatableArray<PrerequisiteEdge> Prerequisites { get; init; }
}
