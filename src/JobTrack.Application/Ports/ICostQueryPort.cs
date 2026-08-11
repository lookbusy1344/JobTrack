namespace JobTrack.Application.Ports;

using Abstractions;
using NodaTime;

/// <summary>
///     The persistence-owned port backing <see cref="ICostQueries" /> (plan §7.3 step 10). Materializes
///     every fact the pure <see cref="Domain.Costing.CostSegmentPartitioner" />/
///     <see
///         cref="Domain.Costing.CostEngine" />
///     pair needs, discovering each contributing worker's
///     database-wide overlapping sessions under an internal elevated read scope (ADR 0017) —
///     <see
///         cref="CostQueries" />
///     performs no I/O or authorization-scoped filtering of its own, only the pure
///     engine calls and the exposure boundary the engine already enforces.
/// </summary>
internal interface ICostQueryPort
{
	/// <summary>
	///     Loads the actor's current roles and <paramref name="nodeId" />'s ancestor-chain owners (ADR
	///     0040: an actor who owns the queried node or an ancestor may view its cost alongside
	///     <see cref="EmployeeRole.Administrator" />/<see cref="EmployeeRole.CostViewer" />), from one
	///     snapshot, for the authorization pre-check <see cref="CostQueries" /> runs before heavy
	///     cost-input materialization (2026-07-25 scalability-follow-up plan §2.4) -- replaces two
	///     separate round trips with one.
	/// </summary>
	/// <exception cref="EntityNotFoundException">The actor does not exist.</exception>
	Task<CostAccessInputs> GetCostAccessInputsAsync(
		AppUserId actorId, JobNodeId nodeId, CancellationToken cancellationToken = default);

	/// <summary>
	///     Materializes the cost inputs for <paramref name="nodeId" />'s subtree as of <paramref name="asOf" />.
	///     The provider rejects subtrees larger than <paramref name="maxHierarchyNodes" /> before
	///     materializing worker sessions and rate data. Callers must authorize (via
	///     <see cref="GetCostAccessInputsAsync" />) before calling this -- it takes no actor id and does
	///     not re-resolve actor roles itself.
	/// </summary>
	Task<CostQueryResult> GetCostInputsAsync(
		JobNodeId nodeId, Instant asOf, int maxHierarchyNodes, CancellationToken cancellationToken = default);

	/// <summary>
	///     Materializes the cost inputs for every candidate in <paramref name="nodeIds" />' subtrees, as a
	///     single union snapshot, as of <paramref name="asOf" /> (fresh-eyes review §2.8: a bounded
	///     listing page's cost enrichment must not open one connection per row). The provider rejects a
	///     union larger than <paramref name="maxHierarchyNodes" /> before materializing worker sessions
	///     and rate data.
	/// </summary>
	Task<BulkCostQueryResult> GetBulkCostInputsAsync(
		AppUserId actorId, EquatableArray<JobNodeId> nodeIds, Instant asOf, int maxHierarchyNodes, CancellationToken cancellationToken = default);
}
