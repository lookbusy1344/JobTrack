namespace JobTrack.Application;

/// <summary>
///     An explicit, versioned write-up change nested inside a composite work-ending command
///     (remediation plan §2.1: <see cref="IWorkCommands.CompleteLeafAsync" /> and
///     <see cref="IWorkCommands.FinishSessionAndUpdateWriteUpAsync" />) -- the containing request's own
///     nullable reference to this type being <see langword="null" /> means "no write-up change";
///     present with <see cref="WriteUp" /> itself <see langword="null" /> means "clear the write-up".
/// </summary>
public sealed record WriteUpChange
{
	/// <summary>The node's expected current optimistic-concurrency version.</summary>
	public required long NodeVersion { get; init; }

	/// <summary>The new write-up text, or <see langword="null" /> to clear it.</summary>
	public string? WriteUp { get; init; }
}
