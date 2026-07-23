namespace JobTrack.Application.Tests;

using Abstractions;

/// <summary>
///     An in-memory fake of <see cref="ICostQueries" /> for <see cref="JobQueriesTests" />'
///     <c>GetJobSubtreeAsync</c> coverage -- <see cref="JobQueries" /> now depends on the facade
///     (reusing its existing authorization and ADR 0002 reconciliation) rather than a lower-level port.
/// </summary>
internal sealed class FakeCostQueries : ICostQueries
{
	private readonly Dictionary<JobNodeId, Money> _bulkCosts = [];
	private readonly HashSet<AppUserId> _deniedActors = [];
	private readonly Dictionary<JobNodeId, Exception> _hierarchyFailures = [];
	private readonly Dictionary<JobNodeId, HierarchyTotalsResult> _totals = [];

	/// <summary>Stage 6 efficiency guard: proves the subtree cost roll-up is one batched call, never per node.</summary>
	public int GetHierarchyTotalsCallCount { get; private set; }

	/// <summary>Fresh-eyes review §2.8 efficiency guard: proves listing enrichment is one batched call, never per row.</summary>
	public int GetBulkNodeCostsCallCount { get; private set; }

	private readonly List<int> _bulkBatchSizes = [];

	/// <summary>The candidate count of each bulk call in order -- lets a caller prove enrichment respects the port's cap.</summary>
	public IReadOnlyList<int> BulkBatchSizes => _bulkBatchSizes;

	public GetBulkNodeCostsRequest? LastBulkRequest { get; private set; }

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

	public Task<BulkNodeCostResult> GetBulkNodeCostsAsync(GetBulkNodeCostsRequest request, CancellationToken cancellationToken = default)
	{
		GetBulkNodeCostsCallCount++;
		LastBulkRequest = request;
		_bulkBatchSizes.Add(request.NodeIds.Count);
		// Mirror the real CostQueries backstop: a batch wider than the cap is a caller contract
		// violation, not something the fake silently absorbs -- so enrichment must chunk to the cap.
		if (request.NodeIds.Count > CostQueries.MaxBulkNodeIdCount) {
			throw new ArgumentOutOfRangeException(
				nameof(request), request.NodeIds.Count, $"A bulk cost request cannot price more than {CostQueries.MaxBulkNodeIdCount} node ids at once.");
		}

		var displayed = request.NodeIds
			.Where(_bulkCosts.ContainsKey)
			.ToDictionary(nodeId => nodeId, nodeId => _bulkCosts[nodeId]);

		return Task.FromResult(new BulkNodeCostResult { DisplayedCosts = EquatableDictionaryFactory.CopyOf(displayed) });
	}

	public void SeedHierarchyTotals(JobNodeId nodeId, HierarchyTotalsResult result) => _totals[nodeId] = result;

	public void DenyActor(AppUserId actorId) => _deniedActors.Add(actorId);

	public void FailHierarchyTotals(JobNodeId nodeId, Exception exception) => _hierarchyFailures[nodeId] = exception;

	public void SeedBulkCost(JobNodeId nodeId, Money cost) => _bulkCosts[nodeId] = cost;
}
