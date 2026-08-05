namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobCommands.ArchiveSubtreeAsync" /> (ADR 0061): the non-destructive
///     alternative to <see cref="DeleteSubtreeRequest" />. Archives <see cref="RootId" /> and every
///     descendant not already archived, in one transaction. Unlike deletion this may be rooted anywhere,
///     including the permanent root, but it fails if any leaf in the subtree has an active session.
/// </summary>
public sealed record ArchiveSubtreeRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The subtree root; it is archived along with every descendant.</summary>
	public required JobNodeId RootId { get; init; }

	/// <summary>The caller's expected current optimistic-concurrency version of <see cref="RootId" />.</summary>
	public required long Version { get; init; }
}
