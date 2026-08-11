namespace JobTrack.Web;

using Abstractions;

/// <summary>
///     The <c>_RequesterStatusIcon</c> partial's model: a requester's public status, rendered with the
///     same state-sign family as Browse achievements while keeping the requester-safe vocabulary.
/// </summary>
public sealed class RequesterStatusIconModel
{
	/// <summary>The public status to draw.</summary>
	public required RequesterStatus Status { get; init; }

	/// <summary>
	///     Whether the status name is visible beside the glyph (the Requests list) or available only
	///     to assistive technology (a subtree row whose title already carries the status marker).
	/// </summary>
	public bool ShowLabel { get; init; } = true;
}
