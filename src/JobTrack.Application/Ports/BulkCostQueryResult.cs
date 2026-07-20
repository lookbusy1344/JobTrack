namespace JobTrack.Application.Ports;

using Abstractions;
using Domain.Hierarchy;
using Domain.Intervals;

/// <summary>
///     Result of <see cref="ICostQueryPort.GetBulkCostInputsAsync" />: one consistent snapshot covering
///     every candidate node's subtree at once (fresh-eyes review §2.8), so a listing page's cost
///     enrichment never opens more than one provider connection regardless of how many rows it holds.
/// </summary>
public sealed record BulkCostQueryResult
{
	/// <summary>The acting user's currently assigned roles.</summary>
	public required EquatableArray<EmployeeRole> ActorRoles { get; init; }

	/// <summary>Every node needed to evaluate any requested candidate's subtree and resolve rate overrides toward each root.</summary>
	public required EquatableDictionary<JobNodeId, HierarchyNode> NodesById { get; init; }

	/// <summary>
	///     Every node's direct owner (or <see langword="null" /> if unassigned), keyed the same as
	///     <see cref="NodesById" /> -- lets <see cref="CostQueries" /> walk a candidate's ancestor chain
	///     for ADR 0040's ownership carve-out entirely in memory, with no further round trips per candidate.
	/// </summary>
	public required EquatableDictionary<JobNodeId, AppUserId?> OwnerUserIdsById { get; init; }

	/// <summary>The costing query's bounds -- from the earliest requested session's start up to the requested <c>asOf</c> instant.</summary>
	public required WorkInterval Bounds { get; init; }

	/// <summary>One <see cref="WorkerCostInputs" /> per worker with a costable session in scope, across every candidate's subtree.</summary>
	public required EquatableArray<WorkerCostInputs> Workers { get; init; }
}
