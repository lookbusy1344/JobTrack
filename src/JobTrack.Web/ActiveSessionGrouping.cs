namespace JobTrack.Web;

using Abstractions;
using Application;

/// <summary>
///     Groups a batch of active sessions by leaf without choosing a representative (plan §2.4:
///     "replace every leaf -&gt; one chosen active session projection with leaf -&gt; all active
///     sessions"). Concurrent per-worker sessions on the same leaf are permitted by design (ownership
///     model §4.2: the "work-session-already-active" constraint is scoped per worker, not per leaf),
///     so a leaf with several active workers keeps every one of them here, in stable order
///     (<c>StartedAt</c> then <c>Id</c>) — never collapsed to a single row candidate the way the
///     previous <c>WorkRowActiveSessions.ByLeaf</c> did. <see cref="ActiveSessionPresentation" /> is
///     the pure helper that derives a viewer/other-workers split and a display-ready count from the
///     groups this produces.
/// </summary>
public static class ActiveSessionGrouping
{
	public static IReadOnlyDictionary<JobNodeId, EquatableArray<WorkSessionResult>> Group(IEnumerable<WorkSessionResult> sessions) =>
		sessions
			.GroupBy(s => s.LeafWorkId)
			.ToDictionary(
				g => g.Key,
				g => (EquatableArray<WorkSessionResult>)[.. g.OrderBy(s => s.StartedAt).ThenBy(s => s.Id.Value)]);
}
