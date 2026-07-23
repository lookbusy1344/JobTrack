namespace JobTrack.Web;

using System.Globalization;
using Abstractions;
using Application;
using Domain.Hierarchy;
using Microsoft.AspNetCore.Mvc.Rendering;
using NodaTime;

/// <summary>
///     The <c>_WorkRowActions</c> partial's model: one leaf row's start/finish cell, as rendered in
///     every table that lists leaves (Browse's subtree and children tables, the awaiting-progress
///     dashboard) and the current-leaf toolbar. Takes the leaf's full active-session collection
///     (never a single "representative" session, plan §2.4) plus the viewer's own id and a
///     server-computed <see cref="CanManage" /> rendering hint, and derives the viewer's own action,
///     every other active worker's exact finish action, and (when authorized) a "Start for…"
///     disclosure — it never infers a target worker from collection order.
/// </summary>
public sealed class WorkRowActionsModel
{
	/// <summary>The leaf this row is for.</summary>
	public required long LeafNodeId { get; init; }

	/// <summary>The signed-in actor, to separate their own session from every other worker's.</summary>
	public required AppUserId ViewerId { get; init; }

	/// <summary>Every active session on this leaf, viewer's own included (plan §2.4: never collapsed to one).</summary>
	public required EquatableArray<WorkSessionResult> ActiveSessions { get; init; }

	/// <summary>
	///     Whether the viewer may manage sessions on this leaf — for another worker's exact finish and
	///     the "Start for…" disclosure (<see cref="IJobQueries.GetSessionManageCapabilitiesAsync" />, a
	///     rendering hint only; the command itself re-validates authorization at write time).
	/// </summary>
	public required bool CanManage { get; init; }

	/// <summary>The leaf's current achievement, used only to suppress invalid start affordances on terminal leaves.</summary>
	public Achievement? Achievement { get; init; }

	/// <summary>Whether the leaf node is archived, used only to suppress invalid start affordances.</summary>
	public bool IsArchived { get; init; }

	/// <summary>The viewing employee's own time zone, for formatting each active session's start time (<see cref="InstantDisplay" />).</summary>
	public required DateTimeZone ViewerZone { get; init; }

	/// <summary>
	///     The Razor Pages handler the one-click start form posts to — <c>Start</c> on Browse,
	///     <c>StartWork</c> on the awaiting-progress dashboard.
	/// </summary>
	public required string StartHandler { get; init; }

	/// <summary>
	///     The field name the start handlers bind the leaf id from — <c>leafNodeId</c> on Browse,
	///     <c>jobNodeId</c> on the awaiting-progress dashboard.
	/// </summary>
	public required string StartNodeFieldName { get; init; }

	/// <summary>
	///     The hosting page's filter/route state, replayed as hidden fields on every form in the cell
	///     so a post lands back on the same filtered view. A null value is still posted, matching how
	///     each page's own bound properties round-trip.
	/// </summary>
	public required IReadOnlyDictionary<string, string?> PageStateFields { get; init; }

	/// <summary>
	///     Every enabled workflow employee for the "Start for…" worker picker — empty (and the
	///     disclosure omitted) when <see cref="CanManage" /> is <see langword="false" />.
	/// </summary>
	public required IReadOnlyList<SelectListItem> StartForWorkerOptions { get; init; }

	/// <summary>
	///     Whether the "Start for…" disclosure renders a visible text label — <see langword="true" />
	///     for the standalone Work/Browse toolbars, where it sits among other labelled buttons;
	///     <see langword="false" /> (the default) for the dense per-row cell, which is icon-only so it
	///     does not out-shout each row's own name.
	/// </summary>
	public bool StartForLabelled { get; init; }

	private ActiveSessionPresentation Presentation => ActiveSessionPresentation.Derive(ActiveSessions, ViewerId);

	/// <summary>
	///     The viewer's own active session on this leaf, if any — the one session these row/toolbar
	///     controls can finish directly. Every other worker's session is managed on the leaf's Sessions
	///     page instead (a leaf may have many simultaneous workers, so an inline finish per worker does
	///     not scale); the plural presentation lives in <see cref="ActiveSessionSummaryModel" />.
	/// </summary>
	public WorkSessionResult? ViewerSession => Presentation.ViewerSession;

	/// <summary>Whether current leaf state prohibits creating or reopening an active session (ADR 0044).</summary>
	public bool IsStartClosed => IsArchived || (Achievement.HasValue && AchievementTransitions.IsCompletedState(Achievement.Value));

	/// <summary>Whether the leaf is paused — started, but nobody clocked on right now (<see cref="LeafActivity.IsPaused" />).</summary>
	public bool IsPaused => LeafActivity.IsPaused(Achievement, ActiveSessions.Count);

	/// <summary>Hidden fields for the viewer's own one-click start.</summary>
	public IReadOnlyDictionary<string, string?> StartFields =>
		new Dictionary<string, string?>(PageStateFields) { [StartNodeFieldName] = LeafNodeId.ToString(CultureInfo.InvariantCulture) };

	/// <summary>The backdate control for the viewer's own one-click start.</summary>
	public BackdateDisclosureModel StartBackdate => new() {
		Handler = StartHandler,
		FieldName = "startedAt",
		Label = "Started at",
		SubmitText = "Start session at this time",
		AccessibleName = "Backdate start",
		HiddenFields = StartFields,
		SubmitClass = "btn btn-primary",
		RowId = $"backdate-start-{LeafNodeId.ToString(CultureInfo.InvariantCulture)}",
	};

	/// <summary>The "Start for…" disclosure, when <see cref="CanManage" /> permits it.</summary>
	public StartForDisclosureModel StartFor => new() {
		Handler = "StartFor",
		RowId = $"start-for-{LeafNodeId.ToString(CultureInfo.InvariantCulture)}",
		NodeFieldName = StartNodeFieldName,
		LeafNodeId = LeafNodeId,
		WorkerOptions = StartForWorkerOptions,
		PageStateFields = PageStateFields,
		Labelled = StartForLabelled,
	};
}
