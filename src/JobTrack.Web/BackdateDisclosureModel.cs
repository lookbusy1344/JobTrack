namespace JobTrack.Web;

/// <summary>
///     Shared model for <c>_BackdateTrigger</c>, <c>_BackdateRow</c>, and <c>_BackdatePanel</c>:
///     everything that differs between the pages offering a backdated start or finish. The trigger
///     button and the <c>datetime-local</c> form it reveals are identical everywhere, so only the
///     handler it posts to, the field it posts under, and the page's own round-trip state vary.
/// </summary>
public sealed class BackdateDisclosureModel
{
	/// <summary>The Razor Pages handler name, e.g. <c>Start</c>, <c>StartWork</c>, or <c>Finish</c>.</summary>
	public required string Handler { get; init; }

	/// <summary>
	///     Stable id of the row/panel this backdate control expands into, and the value its trigger's
	///     <c>aria-controls</c>/<c>data-jt-backdate-toggle</c> point at. Only one of a row's Start/Finish
	///     branches is ever active at once, so a per-leaf or per-session id never collides.
	/// </summary>
	public required string RowId { get; init; }

	/// <summary>The posted field carrying the instant: <c>startedAt</c> or <c>finishedAt</c>.</summary>
	public required string FieldName { get; init; }

	/// <summary>The visible label above the input, e.g. "Started at".</summary>
	public required string Label { get; init; }

	/// <summary>The submit button's text, e.g. "Start at this time".</summary>
	public required string SubmitText { get; init; }

	/// <summary>
	///     The accessible name of the disclosure itself, distinguishing an otherwise identical glyph
	///     when a row offers both a backdated start and a backdated finish.
	/// </summary>
	public required string AccessibleName { get; init; }

	/// <summary>
	///     Hidden fields replayed with the post: the page's filter/route state plus whichever of
	///     node id or session id + version the handler needs. A null value is skipped.
	/// </summary>
	public required IReadOnlyDictionary<string, string?> HiddenFields { get; init; }

	/// <summary>Bootstrap button classes for the submit, matching the action it backdates.</summary>
	public string SubmitClass { get; init; } = "btn btn-secondary";
}
