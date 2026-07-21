namespace JobTrack.Web;

using Abstractions;
using Application;
using NodaTime;

/// <summary>
///     The <c>_ActiveSincePill</c> partial's model: the Active column's state for one leaf, covering
///     zero, one, or several simultaneous workers without ever picking a "representative" session
///     (plan §2.4). Split out of <see cref="WorkRowActionsModel" />'s cell so the tables that list
///     leaves can give it a column of its own — sharing the actions cell made the row's start/finish
///     buttons jump horizontally depending on whether a session was running.
/// </summary>
public sealed class ActiveSessionSummaryModel
{
	/// <summary>
	///     The most active workers a dense per-row pill previews before collapsing the rest into
	///     "+N more" (plan §2.4) — a named cap rather than letting a leaf with many simultaneous
	///     workers make every tree row unbounded. Not applied when <see cref="Compact" /> is
	///     <see langword="false" /> (the toolbar/Sessions-page summary has the width to show everyone).
	/// </summary>
	public const int PreviewLimit = 3;

	/// <summary>Every active session on the leaf, viewer's own included.</summary>
	public required EquatableArray<WorkSessionResult> ActiveSessions { get; init; }

	/// <summary>The signed-in actor, so their own session previews first, marked "You".</summary>
	public required AppUserId ViewerId { get; init; }

	/// <summary>Resolves a worker id to a display name (<see cref="EmployeeDirectoryDisplay.Describe" />), never a bare id.</summary>
	public required Func<AppUserId, string> DescribeWorker { get; init; }

	/// <summary>The viewing employee's own time zone, for formatting a single session's start time.</summary>
	public required DateTimeZone ViewerZone { get; init; }

	/// <summary>The hosting page's own captured "now" (ADR 0016), for the compact today/not-today choice.</summary>
	public required Instant Now { get; init; }

	/// <summary>
	///     Whether to draw the row-sized pill (capped preview, dense wording) or the full toolbar/
	///     Sessions-page form (every active worker named outright, no cap).
	/// </summary>
	public bool Compact { get; init; } = true;

	/// <summary>
	///     Whether the leaf can accept no new session — a terminal achievement or an archived node
	///     (<see cref="WorkRowActionsModel.IsStartClosed" />). Surfaced in the Active column as a plain
	///     "Closed" pill only when no session is currently active, replacing the verbose closure
	///     sentence that used to sit in the Actions cell (which is buttons only).
	/// </summary>
	public bool StartClosed { get; init; }

	/// <summary>The total number of active sessions on this leaf.</summary>
	public int Count => ActiveSessions.Count;

	/// <summary>
	///     The stable preview to display: the viewer's own session first (labelled "You") when they
	///     have one, then every other worker ordered by <see cref="ActiveSessionPresentation.StableOrder" />
	///     (<c>StartedAt</c> then <c>Id</c>) — capped at <see cref="PreviewLimit" /> only when
	///     <see cref="Compact" />.
	/// </summary>
	public IReadOnlyList<ActiveWorkerEntry> PreviewEntries
	{
		get
		{
			var presentation = ActiveSessionPresentation.Derive(ActiveSessions, ViewerId);
			var ordered = new List<ActiveWorkerEntry>(presentation.Count);
			if (presentation.ViewerSession is { } viewerSession) {
				ordered.Add(new(viewerSession, "You", true));
			}

			ordered.AddRange(presentation.OtherSessions.Select(session =>
				new ActiveWorkerEntry(session, DescribeWorker(session.WorkedByUserId), false)));

			return Compact ? [.. ordered.Take(PreviewLimit)] : ordered;
		}
	}

	/// <summary>How many active workers <see cref="PreviewEntries" /> omits — never lost from <see cref="Count" />, only from the preview.</summary>
	public int HiddenCount => Math.Max(0, Count - PreviewEntries.Count);
}

/// <summary>One worker in <see cref="ActiveSessionSummaryModel.PreviewEntries" />.</summary>
public sealed record ActiveWorkerEntry(WorkSessionResult Session, string DisplayName, bool IsViewer);
