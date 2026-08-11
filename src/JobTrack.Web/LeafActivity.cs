namespace JobTrack.Web;

using Abstractions;

/// <summary>
///     The one place the staff UI decides what a leaf's recorded work means beyond its bare
///     <see cref="Achievement" />. A derived label, never a stored column (house style: derived
///     structural labels, ADR 0035's <c>job_node</c> Root/Branch/Leaf as the worked example) — the
///     facts it reads (achievement, active-session count) are already authoritative, so nothing has to
///     be kept in sync.
/// </summary>
public static class LeafActivity
{
	/// <summary>
	///     Whether the leaf is <em>paused</em>: work has started but nobody is clocked on right now.
	///     This is a valid, expected state, not a broken one — ADR 0045 allows zero active sessions
	///     from <see cref="Achievement.InProgress" /> (which is why completing from zero sessions is a
	///     supported path), and it is exactly what "Pause job" produces every time. It is worth naming
	///     only because the alternative is a leaf that reads identically to one nobody has ever
	///     started, when the two call for different next actions.
	/// </summary>
	public static bool IsPaused(Achievement? achievement, int activeSessionCount) =>
		achievement == Achievement.InProgress && activeSessionCount == 0;
}
