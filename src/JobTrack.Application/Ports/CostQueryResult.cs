namespace JobTrack.Application.Ports;

using Abstractions;
using Domain.Hierarchy;
using Domain.Intervals;

/// <summary>
///     Result of <see cref="ICostQueryPort.GetCostInputsAsync" />: the actor's current roles (so
///     <see
///         cref="CostQueries" />
///     can apply <see cref="Domain.Authorization.CostAccessPolicy" /> without a
///     second round-trip), every node needed to evaluate the requested subtree and resolve node-rate
///     overrides up to the root, the query's costing bounds, and one <see cref="WorkerCostInputs" /> per
///     worker who has any costable session in scope.
/// </summary>
public sealed record CostQueryResult
{
	/// <summary>The acting user's currently assigned roles.</summary>
	public required EquatableArray<EmployeeRole> ActorRoles { get; init; }

	/// <summary>Every node needed to evaluate the requested subtree and to resolve rate overrides toward the root.</summary>
	public required EquatableDictionary<JobNodeId, HierarchyNode> NodesById { get; init; }

	/// <summary>The costing query's bounds — from the installation epoch up to the requested <c>asOf</c> instant.</summary>
	public required WorkInterval Bounds { get; init; }

	/// <summary>One <see cref="WorkerCostInputs" /> per worker with a costable session in scope.</summary>
	public required EquatableArray<WorkerCostInputs> Workers { get; init; }
}
