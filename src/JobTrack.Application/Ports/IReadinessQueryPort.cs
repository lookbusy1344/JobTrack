namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     The persistence-owned port backing <see cref="IJobQueries.GetReadinessAsync" /> (plan §7.3 step
///     5). Materializes every fact the pure <see cref="Domain.Hierarchy.ReadinessCalculator" /> needs;
///     <see cref="JobQueries" /> performs no graph traversal of its own.
/// </summary>
internal interface IReadinessQueryPort
{
	/// <inheritdoc cref="IJobQueries.GetReadinessAsync" />
	Task<ReadinessQueryResult> GetReadinessInputsAsync(JobNodeId nodeId, CancellationToken cancellationToken = default);

	/// <summary>
	///     Batch form of <see cref="GetReadinessInputsAsync" />: one materialization covering every node in
	///     <paramref name="nodeIds" />, each one's own ancestor chain, every prerequisite declared on any
	///     of them, and the complete subtree of every required job. Unlike the single-node form, the
	///     requested nodes need not share an ancestor relationship with one another (e.g. sibling rows in
	///     a displayed subtree) -- <see cref="JobQueries" /> uses this so readiness can be evaluated for
	///     every row of a subtree from one bounded snapshot assembly instead of reusing one row's own
	///     ancestor-scoped result for rows it does not actually cover. Command count is constant with
	///     respect to requested-node and prerequisite count.
	/// </summary>
	Task<ReadinessQueryResult> GetReadinessInputsForNodesAsync(
		IReadOnlyCollection<JobNodeId> nodeIds, CancellationToken cancellationToken = default);
}
