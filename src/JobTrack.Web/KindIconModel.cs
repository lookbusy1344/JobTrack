namespace JobTrack.Web;

using Abstractions;

/// <summary>The <c>_KindIcon</c> partial's model: a node's root/branch/leaf glyph, wherever its kind is shown outside a tree row.</summary>
public sealed class KindIconModel
{
	/// <summary>The kind to draw the glyph for.</summary>
	public required NodeKind Kind { get; init; }

	/// <summary>
	///     Whether the kind's name renders as visible text beside the glyph (a list item, a detail
	///     panel) or stays visually-hidden only (a table cell too narrow to spare the width) --
	///     either way the glyph itself is <c>aria-hidden</c>, so the fact reaches assistive tech
	///     through this text, never through colour or shape alone.
	/// </summary>
	public bool ShowLabel { get; init; } = true;

	/// <summary>
	///     Whether a visible label (see <see cref="ShowLabel" />) hides itself below the Bootstrap
	///     <c>lg</c> breakpoint, leaving just the glyph on a narrow-to-tablet screen -- for a list
	///     item that already crowds a title and a status pill onto one line and can't spare the
	///     width there.
	/// </summary>
	public bool CollapseLabelOnNarrow { get; init; }
}
