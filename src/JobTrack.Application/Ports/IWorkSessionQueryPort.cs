namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     The persistence-owned port backing <see cref="IJobQueries.GetLeafSessionsAsync" /> (plan §8.5
///     slice 4). Loads the requested sessions on the given leaf alongside the actor's current
///     roles in one round-trip, the same shape as <see cref="IEmployeeQueryPort" />, so
///     <see cref="JobQueries" /> can apply <see cref="Domain.Authorization.WorkSessionAccessPolicy" />
///     without a second round-trip.
/// </summary>
public interface IWorkSessionQueryPort
{
	/// <summary>
	///     Loads sessions on the leaf and the actor's current roles, sessions ordered
	///     most-recent-first by <c>StartedAt</c> then <c>Id</c>, bounded by <paramref name="offset" />/
	///     <paramref name="limit" /> (remediation plan §3.1) — a <see langword="null" />
	///     <paramref name="limit" /> returns every session, unbounded. A <see langword="null" />
	///     <paramref name="workedByUserId" /> returns every worker's sessions on the leaf rather than
	///     narrowing to one; the port applies no authorization of its own either way
	///     (<see cref="JobQueries" /> owns that, from the roles returned alongside).
	/// </summary>
	/// <exception cref="EntityNotFoundException">The actor or the leaf job node does not exist.</exception>
	Task<WorkSessionQueryResult> GetSessionsAsync(
		AppUserId actorId, JobNodeId leafWorkId, AppUserId? workedByUserId,
		int offset = 0, int? limit = null, CancellationToken cancellationToken = default);

	/// <summary>
	///     Loads every worker's unfinished session among the given leaves and the actor's current
	///     roles, the same "no actor-based filtering" shape as <see cref="GetSessionsAsync" />'s
	///     null-worker default (ADR 0041) -- <see cref="JobQueries" /> is the layer that narrows this
	///     to what the querying actor may see. A leaf id that no longer resolves is silently omitted,
	///     matching <see cref="IJobBrowseQueryPort.GetSummariesByIdsAsync" />.
	/// </summary>
	/// <exception cref="EntityNotFoundException">The actor does not exist.</exception>
	Task<WorkSessionQueryResult> GetActiveSessionsAsync(
		AppUserId actorId, EquatableArray<JobNodeId> leafWorkIds, CancellationToken cancellationToken = default);

	/// <summary>
	///     Loads the actor's current roles and, among <paramref name="leafWorkIds" />, which ones the
	///     actor directly owns or has an owning ancestor of, in one round trip regardless of how many
	///     leaf ids are given (ADR 0044/Stage 4 of the browse-sessions plan: the batched read model
	///     backing a per-leaf <c>CanManageSessions</c> capability, so Razor can decide what to render
	///     without a per-row ancestor-ownership query). <see cref="JobQueries" /> combines this with
	///     <see cref="Domain.Authorization.WorkSessionAccessPolicy.CanManage" /> per leaf — this port
	///     applies no policy of its own, only loads the facts.
	/// </summary>
	/// <exception cref="EntityNotFoundException">The actor does not exist.</exception>
	Task<WorkSessionManageCapabilityQueryResult> GetManageCapabilitiesAsync(
		AppUserId actorId, EquatableArray<JobNodeId> leafWorkIds, CancellationToken cancellationToken = default);
}
