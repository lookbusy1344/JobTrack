namespace JobTrack.Application.Tests;

using Abstractions;

/// <summary>
///     An in-memory fake of <see cref="ICostQueries" /> for <see cref="JobQueriesTests" />'
///     <c>GetJobSubtreeAsync</c> coverage -- <see cref="JobQueries" /> now depends on the facade
///     (reusing its existing authorization and ADR 0002 reconciliation) rather than a lower-level port.
/// </summary>
internal sealed class FakeCostQueries : ICostQueries
{
	private readonly HashSet<AppUserId> _deniedActors = [];
	private readonly Dictionary<JobNodeId, Exception> _hierarchyFailures = [];
	private readonly Dictionary<JobNodeId, HierarchyTotalsResult> _totals = [];

	/// <summary>Stage 6 efficiency guard: proves the subtree cost roll-up is one batched call, never per node.</summary>
	public int GetHierarchyTotalsCallCount { get; private set; }

	public Task<CostDetailsResult> GetCostDetailsAsync(
		GetCostDetailsRequest request, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException($"{nameof(FakeCostQueries)} only backs {nameof(ICostQueries.GetHierarchyTotalsAsync)}.");

	public Task<HierarchyTotalsResult> GetHierarchyTotalsAsync(
		GetHierarchyTotalsRequest request, CancellationToken cancellationToken = default)
	{
		GetHierarchyTotalsCallCount++;
		if (_deniedActors.Contains(request.Context.Actor)) {
			throw new AuthorizationDeniedException($"Actor {request.Context.Actor} may not view costs for node {request.NodeId}.");
		}

		if (_hierarchyFailures.TryGetValue(request.NodeId, out var failure)) {
			throw failure;
		}

		if (!_totals.TryGetValue(request.NodeId, out var result)) {
			throw new EntityNotFoundException($"Job node {request.NodeId} does not exist.");
		}

		return Task.FromResult(result);
	}

	public void SeedHierarchyTotals(JobNodeId nodeId, HierarchyTotalsResult result) => _totals[nodeId] = result;

	public void DenyActor(AppUserId actorId) => _deniedActors.Add(actorId);

	public void FailHierarchyTotals(JobNodeId nodeId, Exception exception) => _hierarchyFailures[nodeId] = exception;
}
