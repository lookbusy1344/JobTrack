namespace JobTrack.Web;

/// <summary>
///     The <c>_WriteUpField</c> partial's model: the node's write-up as a label, a multi-line field, and
///     the node's own concurrency version, with no surrounding <c>form</c> or button of its own. Rendered
///     inside whichever form owns it — on <c>/Jobs/Work</c> that is the single ending form shared with
///     Pause and Complete, so the write-up saves with whichever of the three buttons is pressed.
/// </summary>
public sealed class WriteUpFieldModel
{
	/// <summary>The node's optimistic-concurrency version as the page rendered it, posted as <c>nodeVersion</c>.</summary>
	public required long NodeVersion { get; init; }

	/// <summary>The currently stored write-up, or <see langword="null" /> when nobody has written one yet.</summary>
	public required string? WriteUp { get; init; }
}
