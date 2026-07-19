namespace JobTrack.Web;

using System.Globalization;
using Application;
using NodaTime;

/// <summary>
///     The <c>_WorkRowActions</c> partial's model: one leaf row's start/finish cell, as rendered in
///     every table that lists leaves (Browse's subtree and children tables, the awaiting-progress
///     dashboard). The cell is a closed either/or — a leaf with an active session offers finish, one
///     without offers start — and each side carries its own backdate disclosure, so the whole thing
///     is decided by whether <see cref="ActiveSession" /> is present.
/// </summary>
public sealed class WorkRowActionsModel
{
	/// <summary>The leaf this row is for.</summary>
	public required long LeafNodeId { get; init; }

	/// <summary>
	///     The active session for <see cref="LeafNodeId" />, or <c>null</c> when the leaf has none and
	///     the row should offer to start work instead.
	/// </summary>
	public WorkSessionResult? ActiveSession { get; init; }

	/// <summary>
	///     <see cref="ActiveSession" />'s worker, when it is not the viewing actor's own session --
	///     surfaced when a privileged actor (Administrator/JobManager, ADR 0032) sees another worker's
	///     active session here and can finish it. <see langword="null" /> for the actor's own session,
	///     which needs no such label.
	/// </summary>
	public string? ActiveSessionWorkedByOther { get; init; }

	/// <summary>The viewing employee's own time zone, for formatting <see cref="ActiveSession" />'s start time (<see cref="InstantDisplay" />).</summary>
	public required DateTimeZone ViewerZone { get; init; }

	/// <summary>
	///     The Razor Pages handler the start form posts to — <c>Start</c> on Browse,
	///     <c>StartWork</c> on the awaiting-progress dashboard.
	/// </summary>
	public required string StartHandler { get; init; }

	/// <summary>
	///     The field name the start handler binds the leaf id from — <c>leafNodeId</c> on Browse,
	///     <c>jobNodeId</c> on the awaiting-progress dashboard.
	/// </summary>
	public required string StartNodeFieldName { get; init; }

	/// <summary>
	///     The hosting page's filter/route state, replayed as hidden fields on every form in the cell
	///     so a post lands back on the same filtered view. A null value is still posted, matching how
	///     each page's own bound properties round-trip.
	/// </summary>
	public required IReadOnlyDictionary<string, string?> PageStateFields { get; init; }

	/// <summary>Hidden fields for a form acting on the leaf itself (start).</summary>
	public IReadOnlyDictionary<string, string?> StartFields =>
		new Dictionary<string, string?>(PageStateFields) { [StartNodeFieldName] = LeafNodeId.ToString(CultureInfo.InvariantCulture) };

	/// <summary>Hidden fields for a form acting on the active session (finish), including its version.</summary>
	public IReadOnlyDictionary<string, string?> FinishFields =>
		ActiveSession is { } session
			? new Dictionary<string, string?>(PageStateFields) {
				["sessionId"] = session.Id.Value.ToString(CultureInfo.InvariantCulture),
				["version"] = session.Version.ToString(CultureInfo.InvariantCulture),
			}
			: throw new InvalidOperationException("There is no active session to finish.");

	/// <summary>
	///     The backdate control for whichever branch (start or finish) this row is currently offering —
	///     the pair to <see cref="_BackdateRow" />/<see cref="_BackdateTrigger" />. <see cref="LeafNodeId" />
	///     alone makes the row id stable and unique: a leaf offers only one of start or finish at a time.
	/// </summary>
	public BackdateDisclosureModel Backdate =>
		ActiveSession is not null
			? new() {
				Handler = "Finish",
				FieldName = "finishedAt",
				Label = "Finished at",
				SubmitText = "Finish at this time",
				AccessibleName = "Backdate finish",
				HiddenFields = FinishFields,
				RowId = $"backdate-finish-{LeafNodeId.ToString(CultureInfo.InvariantCulture)}",
			}
			: new BackdateDisclosureModel {
				Handler = StartHandler,
				FieldName = "startedAt",
				Label = "Started at",
				SubmitText = "Start at this time",
				AccessibleName = "Backdate start",
				HiddenFields = StartFields,
				SubmitClass = "btn btn-primary",
				RowId = $"backdate-start-{LeafNodeId.ToString(CultureInfo.InvariantCulture)}",
			};
}
