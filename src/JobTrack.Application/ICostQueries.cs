namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Cost queries (plan §7.3 step 10: calculate cost details and hierarchy totals;
///     docs/api/jobtrack-client-design.md). Gated by <see cref="Domain.Authorization.CostAccessPolicy" />
///     — unlike <see cref="IJobQueries.GetReadinessAsync" />, cost visibility is never an unqualified
///     baseline capability (spec §7.3).
/// </summary>
public interface ICostQueries
{
	/// <summary>Calculates one node's exact and displayed cost, with its rate-provenance segment trace.</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor does not hold cost-viewing permission (see <see cref="Domain.Authorization.CostAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The node does not exist.</exception>
	/// <exception cref="MissingRateException">No rate source resolves for a contributing session.</exception>
	Task<CostDetailsResult> GetCostDetailsAsync(GetCostDetailsRequest request, CancellationToken cancellationToken = default);

	/// <summary>Calculates reconciled hierarchy cost totals for a node and its entire subtree.</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor does not hold cost-viewing permission (see <see cref="Domain.Authorization.CostAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The node does not exist.</exception>
	/// <exception cref="MissingRateException">No rate source resolves for a contributing session.</exception>
	Task<HierarchyTotalsResult> GetHierarchyTotalsAsync(
		GetHierarchyTotalsRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Prices every candidate in <see cref="GetBulkNodeCostsRequest.NodeIds" /> from one materialized
	///     snapshot (fresh-eyes review §2.8) -- a bounded listing page's row-by-row cost enrichment, never
	///     one <see cref="GetHierarchyTotalsAsync" /> round trip per row. A candidate the actor may not
	///     view the individual cost of (ADR 0040/0042) is simply absent from the result, never a
	///     whole-request failure or a broadened disclosure.
	/// </summary>
	/// <exception cref="ArgumentOutOfRangeException">
	///     <see cref="GetBulkNodeCostsRequest.NodeIds" /> exceeds the bulk request's own size cap.
	/// </exception>
	Task<BulkNodeCostResult> GetBulkNodeCostsAsync(GetBulkNodeCostsRequest request, CancellationToken cancellationToken = default);
}
