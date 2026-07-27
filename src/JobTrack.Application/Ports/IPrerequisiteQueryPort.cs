namespace JobTrack.Application.Ports;

using Abstractions;
using Domain.Hierarchy;

/// <summary>
///     The persistence-owned port backing <see cref="IJobQueries.GetPrerequisitesAsync" /> (plan §8.5
///     slice 5). Carries no actor — the query itself has no authorization gate (see
///     <see cref="GetPrerequisitesRequest" />).
/// </summary>
internal interface IPrerequisiteQueryPort
{
	/// <summary>
	///     Counts edges for which <paramref name="requiredJobId" /> is the required side, without
	///     materializing the touching edge collection. Used by bounded page projections that need only
	///     dependent impact, not the edge details.
	/// </summary>
	/// <exception cref="EntityNotFoundException">The node does not exist.</exception>
	Task<int> CountDirectDependentsAsync(JobNodeId requiredJobId, CancellationToken cancellationToken = default);

	/// <summary>
	///     Whether any job that directly requires <paramref name="requiredJobId" />, or any leaf beneath
	///     such a job, currently holds an active <c>WorkSession</c>, so a page can warn before an actor
	///     reopens a successful prerequisite out from under work that is running right now (ADR 0045 §2's
	///     dependent-impact warning). Purely advisory: under ADR 0051 the reopen itself is permitted in
	///     that state, and no command gates on this — the block lands on the dependent's own completion.
	/// </summary>
	/// <exception cref="EntityNotFoundException">The node does not exist.</exception>
	Task<bool> HasActiveDependentWorkAsync(JobNodeId requiredJobId, CancellationToken cancellationToken = default);

	/// <summary>
	///     Loads every prerequisite edge touching the node, as either <see cref="PrerequisiteEdge.RequiredJobId" />
	///     or <see cref="PrerequisiteEdge.DependentJobId" />, ordered by <c>RequiredJobId</c> then
	///     <c>DependentJobId</c>, bounded by <paramref name="offset" />/<paramref name="limit" />
	///     (remediation plan §3.1) — a <see langword="null" /> <paramref name="limit" /> returns every
	///     edge, unbounded.
	/// </summary>
	/// <exception cref="EntityNotFoundException">The node does not exist.</exception>
	Task<EquatableArray<PrerequisiteEdge>> GetPrerequisitesAsync(
		JobNodeId nodeId, int offset = 0, int? limit = null, CancellationToken cancellationToken = default);
}
