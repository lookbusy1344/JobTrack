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
}
