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

	/// <summary>
	///     The text this model renders: always the four-letter-or-shorter form. A priority is a
	///     one-glance ordering fact that reads the same wherever it appears, and its widest home --
	///     a table column sharing row width with five others -- sets the form for all of them.
	/// </summary>
	public string Text => AbbreviatedLabel(Priority);

	private static string AbbreviatedLabel(Priority priority) => priority switch {
		Priority.Low => "Low",
		Priority.Medium => "Med",
		Priority.High => "High",
		Priority.Urgent => "Urgt",
		_ => throw new ArgumentOutOfRangeException(nameof(priority), priority, null),
	};
}
