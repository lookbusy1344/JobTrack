namespace JobTrack.Application.Ports;

using Abstractions;
using Domain.Hierarchy;

/// <summary>
///     The persistence-owned port backing <see cref="IJobQueries" />'s job-tree browsing, search,
///     ownership, and archive-filter queries (plan §8.5 slice 2). Carries no actor-role bundling, since
///     these queries carry no authorization gate (see <see cref="GetJobNodeRequest" />).
/// </summary>
internal interface IJobBrowseQueryPort
{
	/// <summary>Loads a node's full detail and root-first ancestor breadcrumb.</summary>
	/// <param name="nodeId">The node to retrieve, or <see langword="null" /> for the permanent root.</param>
	/// <param name="cancellationToken">Cancels the query.</param>
	/// <exception cref="EntityNotFoundException">The node does not exist.</exception>
	Task<JobNodeDetailResult> GetNodeAsync(JobNodeId? nodeId, CancellationToken cancellationToken = default);

	/// <summary>
	///     Loads a node's direct children, filtered by owner and archive scope, ordered by <c>Id</c>,
	///     bounded by <paramref name="offset" />/<paramref name="limit" /> (remediation plan §3.1) — a
	///     <see langword="null" /> <paramref name="limit" /> returns every match, unbounded.
	/// </summary>
	/// <exception cref="EntityNotFoundException">The parent node does not exist.</exception>
	Task<EquatableArray<JobNodeSummaryResult>> GetChildrenAsync(
		JobNodeId parentId, OwnershipFilter ownership, JobArchiveFilter archiveFilter,
		int offset = 0, int? limit = null, CancellationToken cancellationToken = default);

	/// <summary>
	///     Searches every node's <c>Description</c> for a case-insensitive substring match, filtered by
	///     owner and archive scope, ordered by <c>Id</c>, bounded by <paramref name="offset" />/
	///     <paramref name="limit" /> (remediation plan §3.1) — a <see langword="null" />
	///     <paramref name="limit" /> returns every match, unbounded.
	/// </summary>
	Task<EquatableArray<JobNodeSummaryResult>> SearchJobNodesAsync(
		string searchText, OwnershipFilter ownership, JobArchiveFilter archiveFilter,
		int offset = 0, int? limit = null, CancellationToken cancellationToken = default);

	/// <summary>
	///     Loads whatever subset of <paramref name="ids" /> currently resolves to a node, archived or
	///     not. Unlike <see cref="GetNodeAsync" />, an id that no longer resolves is silently omitted
	///     rather than throwing -- this is an opportunistic describe-what-you-can lookup for rendering
	///     links to a caller-supplied set of ids (prerequisite/dependent edges), not a single
	///     required-entity fetch.
	/// </summary>
	Task<EquatableArray<JobNodeSummaryResult>> GetSummariesByIdsAsync(
		EquatableArray<JobNodeId> ids, CancellationToken cancellationToken = default);

	/// <summary>
	///     Loads a bounded multi-level subtree rooted at <paramref name="rootId" /> (ADR 0039): every
	///     immediate child of the root, and for every node whose children are expanded to a further
	///     level, only the first <see cref="Domain.Hierarchy.JobSubtreeLimits.BreadthCap" /> children (by
	///     <c>Id</c> order) recurse further -- the rest still appear with
	///     <see cref="JobNodeSubtreeRow.HasUnexpandedChildren" /> set. Recursion never goes past
	///     <paramref name="maxDepth" /> levels below the root. <paramref name="ownership" />/
	///     <paramref name="archiveFilter" /> use structural pass-through: a non-matching ancestor of a
	///     matching descendant still renders (<see cref="JobNodeSubtreeRow.MatchesFilter" /> distinguishes
	///     the two).
	/// </summary>
	/// <exception cref="EntityNotFoundException">The root node does not exist.</exception>
	/// <exception cref="ArgumentOutOfRangeException">
	///     <paramref name="maxDepth" /> is negative or exceeds <see cref="Domain.Hierarchy.JobSubtreeLimits.HardMaxDepth" />.
	/// </exception>
	Task<EquatableArray<JobNodeSubtreeRow>> GetSubtreeAsync(
		JobNodeId rootId, int maxDepth, OwnershipFilter ownership, JobArchiveFilter archiveFilter,
		CancellationToken cancellationToken = default);
}
