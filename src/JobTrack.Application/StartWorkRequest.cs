namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     Input to <see cref="IWorkCommands.StartWorkAsync" />: the one-click composite of attaching
///     <c>LeafWork</c> if the node doesn't already have it, advancing a freshly-<see cref="Achievement.Waiting" />
///     leaf to <see cref="Achievement.InProgress" />, and starting a session -- all inside one
///     transaction. Unlike <see cref="StartSessionRequest" />, <see cref="JobNodeId" /> need not already
///     have <c>LeafWork</c> attached.
/// </summary>
public sealed record StartWorkRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The leaf job node to start work on.</summary>
	public required JobNodeId JobNodeId { get; init; }

	/// <summary>The employee performing this session's work.</summary>
	public required AppUserId WorkedByUserId { get; init; }

	/// <summary>
	///     The session's start instant, or <see langword="null" /> to capture "now". Must not be in the
	///     future (ADR 0028).
	/// </summary>
	public Instant? StartedAt { get; init; }
}
