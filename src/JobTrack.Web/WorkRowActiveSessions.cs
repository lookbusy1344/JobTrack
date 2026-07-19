namespace JobTrack.Web;

using Abstractions;
using Application;

/// <summary>
///     Keys a batch of active sessions by leaf for <see cref="WorkRowActionsModel.ActiveSession" />.
///     Concurrent per-worker sessions on the same leaf are permitted by design (ownership model §4.2:
///     the "work-session-already-active" constraint is scoped per worker, not per leaf), so once a
///     privileged actor sees every worker's active session on a leaf
///     (<see cref="IJobQueries.GetActiveSessionsAsync" />, ADR 0032/0041) there can be more than one
///     row candidate per key -- this picks the one that started first, a stable and deterministic
///     choice, rather than letting a plain <c>ToDictionary</c> throw on the duplicate key.
/// </summary>
public static class WorkRowActiveSessions
{
	public static IReadOnlyDictionary<JobNodeId, WorkSessionResult> ByLeaf(IEnumerable<WorkSessionResult> sessions) =>
		sessions
			.GroupBy(s => s.LeafWorkId)
			.ToDictionary(g => g.Key, g => g.OrderBy(s => s.StartedAt).First());
}
