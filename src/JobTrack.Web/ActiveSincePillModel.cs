namespace JobTrack.Web;

using Application;
using NodaTime;

/// <summary>
///     The <c>_ActiveSincePill</c> partial's model: the "Active since …" pill for a leaf with a
///     running work session. Split out of <see cref="WorkRowActionsModel" />'s cell so the tables
///     that list leaves can give it a column of its own — sharing the actions cell made the row's
///     start/finish buttons jump horizontally depending on whether a session was running.
/// </summary>
public sealed class ActiveSincePillModel
{
	/// <summary>
	///     The running session, or <see langword="null" /> when the leaf has none — in which case the
	///     partial renders nothing, leaving the column empty for that row.
	/// </summary>
	public WorkSessionResult? ActiveSession { get; init; }

	/// <summary>
	///     <see cref="ActiveSession" />'s worker, when it is not the viewing actor's own session —
	///     surfaced when a privileged actor (Administrator/JobManager, ADR 0032) sees another worker's
	///     active session. <see langword="null" /> for the actor's own session, which needs no label.
	/// </summary>
	public string? ActiveSessionWorkedByOther { get; init; }

	/// <summary>The viewing employee's own time zone, for formatting the session's start time.</summary>
	public required DateTimeZone ViewerZone { get; init; }

	/// <summary>
	///     Whether to draw the row-sized pill — stopwatch plus a compact timestamp, no wrapping, the
	///     words carried only for assistive tech. <see langword="false" /> gives the full "Active
	///     since …" wording for a standalone toolbar, which has the width for it and where a bare
	///     timestamp beside a row of buttons would read as unlabelled.
	/// </summary>
	public bool Compact { get; init; } = true;
}
