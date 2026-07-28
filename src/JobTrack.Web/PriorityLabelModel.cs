namespace JobTrack.Web;

using Abstractions;

/// <summary>
///     The <c>_PriorityLabel</c> partial's model: a node's priority rendered as text, styled per level
///     wherever a priority appears (Browse's node detail and subtree table, AwaitingProgress). Colour
///     rides on a per-level modifier class rather than an inline style, so a level earns emphasis by
///     its own rule in <c>site.css</c> -- currently <see cref="Abstractions.Priority.High" /> and
///     <see cref="Abstractions.Priority.Urgent" /> (red, bold); Low/Medium render as plain text until a future pass
///     gives each its own treatment.
/// </summary>
public sealed class PriorityLabelModel
{
	/// <summary>The priority to render.</summary>
	public required Priority Priority { get; init; }
}
