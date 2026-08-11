namespace JobTrack.Web;

using Abstractions;
using Application;

/// <summary>
///     Pure derivation over one leaf's active-session collection (plan §2.4/Stage 4): the viewer's own
///     active session if any, every other worker's active session, the total count, and a stable
///     display order — without ever choosing a single "representative" session. Takes no dependency on
///     a database or the employee directory; a caller resolves display names separately from the
///     already-batched directory (<see cref="EmployeeDirectoryDisplay" />), keeping this helper a plain
///     function of its inputs and directly unit-testable.
/// </summary>
public sealed record ActiveSessionPresentation
{
	/// <summary>The viewer's own active session on this leaf, if they have one.</summary>
	public required WorkSessionResult? ViewerSession { get; init; }

	/// <summary>Every other worker's active session on this leaf, in stable order.</summary>
	public required EquatableArray<WorkSessionResult> OtherSessions { get; init; }

	/// <summary>The total number of active sessions on this leaf (viewer's own included).</summary>
	public required int Count { get; init; }

	/// <summary>Every active session on this leaf, viewer's own included, in stable order (<c>StartedAt</c> then <c>Id</c>).</summary>
	public required EquatableArray<WorkSessionResult> StableOrder { get; init; }

	/// <summary>Derives the presentation for one leaf's active-session collection and the given viewer.</summary>
	public static ActiveSessionPresentation Derive(IReadOnlyCollection<WorkSessionResult> sessions, AppUserId viewerId)
	{
		ArgumentNullException.ThrowIfNull(sessions);

		EquatableArray<WorkSessionResult> ordered = [.. sessions.OrderBy(s => s.StartedAt).ThenBy(s => s.Id.Value)];
		var viewerSession = ordered.FirstOrDefault(s => s.WorkedByUserId == viewerId);
		EquatableArray<WorkSessionResult> others = [.. ordered.Where(s => s.WorkedByUserId != viewerId)];

		return new() { ViewerSession = viewerSession, OtherSessions = others, Count = ordered.Count, StableOrder = ordered };
	}
}
