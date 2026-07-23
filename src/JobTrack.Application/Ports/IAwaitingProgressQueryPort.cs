namespace JobTrack.Application.Ports;

using Domain.Hierarchy;

/// <summary>
///     The persistence-owned port backing <see cref="IJobQueries.GetAwaitingProgressAsync" />.
///     Materializes every fact the pure <see cref="AwaitingProgressCalculator" /> needs — the complete
///     <c>job_node</c> graph (same full-table load precedent as <see cref="IReadinessQueryPort" />),
///     every node's display/filter/sort facts, and every prerequisite edge — so <see cref="JobQueries" />
///     performs no graph traversal of its own. Carries no actor — the query itself has no
///     authorization gate (see <see cref="GetAwaitingProgressRequest" />).
/// </summary>
internal interface IAwaitingProgressQueryPort
{
	/// <inheritdoc cref="IJobQueries.GetAwaitingProgressAsync" />
	Task<AwaitingProgressQueryResult> GetAwaitingProgressInputsAsync(CancellationToken cancellationToken = default);
}
