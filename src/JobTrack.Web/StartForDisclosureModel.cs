namespace JobTrack.Web;

using Microsoft.AspNetCore.Mvc.Rendering;

/// <summary>
///     Shared model for <c>_StartForTrigger</c>'s native progressive disclosure: the "Start for…"
///     control (plan §2.5) that lets a user authorized to manage sessions
///     on a leaf (<see cref="WorkRowActionsModel.CanManage" />) start a session for a worker other than
///     themselves. Deliberately posts <see cref="StartForFieldName" /> (<c>StartForUserId</c>), never
///     the Sessions history filter field — the picker's target worker and "whose history is being
///     viewed" are separate concerns that must never be conflated (plan §2.5 rule 4).
/// </summary>
public sealed class StartForDisclosureModel
{
	/// <summary>The posted field naming the target worker — distinct from the Sessions history filter.</summary>
	public const string StartForFieldName = "StartForUserId";

	/// <summary>The Razor Pages handler name: <c>StartFor</c> on every page offering this disclosure.</summary>
	public required string Handler { get; init; }

	/// <summary>
	///     Whether the disclosure's trigger renders a visible "Start for…" text label (standalone
	///     toolbars on Work/Browse, where it sits beside other labelled buttons) rather than the
	///     icon-only summary the dense per-row cell (<c>_WorkRowActions</c>) deliberately uses so it
	///     does not out-shout each row's own name. Defaults to icon-only; toolbar call sites opt in
	///     with <c>with { Labelled = true }</c>.
	/// </summary>
	public bool Labelled { get; init; }

	/// <summary>Stable id of the row/panel this control expands into, and its trigger's <c>aria-controls</c>/<c>data-jt-disclosure-toggle</c>.</summary>
	public required string RowId { get; init; }

	/// <summary>The posted field naming the leaf to start work on.</summary>
	public required string NodeFieldName { get; init; }

	/// <summary>The leaf id, as the value for <see cref="NodeFieldName" />.</summary>
	public required long LeafNodeId { get; init; }

	/// <summary>Every enabled workflow employee, for the worker <c>&lt;select&gt;</c> — never the actor themselves (they have the one-click Start).</summary>
	public required IReadOnlyList<SelectListItem> WorkerOptions { get; init; }

	/// <summary>Hidden fields replayed with the post: the page's own filter/route state.</summary>
	public required IReadOnlyDictionary<string, string?> PageStateFields { get; init; }
}
