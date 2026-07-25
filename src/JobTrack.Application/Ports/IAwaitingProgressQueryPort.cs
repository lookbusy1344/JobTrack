namespace JobTrack.Application.Ports;

using Domain.Hierarchy;

/// <summary>
///     The persistence-owned port backing <see cref="IJobQueries.GetAwaitingProgressAsync" />.
///     Materializes every fact the pure <see cref="AwaitingProgressCalculator" /> needs, narrowed to the
///     supplied <see cref="AwaitingProgressQueryFilter" />'s own ownership/subtree/search/paging scope
///     (2026-07-25 scalability-follow-up plan §2.1, on top of the 2026-07-24 §2.2 step 4 narrowing to
///     currently-unfinished leaves) — never every unfinished leaf in the installation — plus the
///     ancestor/required-job facts readiness needs, so <see cref="JobQueries" /> performs no graph
///     traversal or in-memory filtering/paging of its own. Carries no actor — the query itself has no
///     authorization gate (see <see cref="GetAwaitingProgressRequest" />).
/// </summary>
internal interface IAwaitingProgressQueryPort
{
	/// <summary>
	///     <inheritdoc cref="IJobQueries.GetAwaitingProgressAsync" path="/summary" />
	/// </summary>
	/// <param name="filter">The requested ownership/subtree/search/paging scope.</param>
	/// <param name="cancellationToken">Propagates cancellation.</param>
	Task<AwaitingProgressQueryResult> GetAwaitingProgressInputsAsync(
		AwaitingProgressQueryFilter filter, CancellationToken cancellationToken = default);
}
