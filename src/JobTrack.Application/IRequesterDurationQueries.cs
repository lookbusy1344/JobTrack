namespace JobTrack.Application;

using Abstractions;
using Domain.Costing;
using NodaTime;

/// <summary>
///     Internal requester-detail projection of concurrency-allocated work duration. It deliberately
///     exposes neither money nor session records. The caller must first authorize access to the
///     request itself; this interface does not perform an independent cost-role check.
/// </summary>
internal interface IRequesterDurationQueries
{
	/// <summary>Returns exact allocated-duration totals for every node in the request subtree.</summary>
	Task<EquatableDictionary<JobNodeId, AllocatedDuration>> GetRequesterVisibleHierarchyAsync(
		JobNodeId nodeId, Instant asOf, CancellationToken cancellationToken = default);
}
