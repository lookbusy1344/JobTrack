namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     Input to <see cref="IWorkCommands.FinishSessionAndUpdateWriteUpAsync" />: the atomic composite
///     (remediation plan §2.1) that finishes one active session and applies an optional write-up
///     change to its leaf's node, in one commit -- rather than the web host coordinating
///     <see cref="IWorkCommands.FinishSessionAsync" /> and a separate <see cref="IJobCommands.EditAsync" />
///     call as two independent mutations.
/// </summary>
public sealed record FinishSessionAndUpdateWriteUpRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The active session being finished.</summary>
	public required WorkSessionId SessionId { get; init; }

	/// <summary>The caller's expected current optimistic-concurrency version for the session.</summary>
	public required long Version { get; init; }

	/// <summary>
	///     The session's finish instant, or <see langword="null" /> to capture "now". Must be after the
	///     session's start instant and must not be in the future (ADR 0028).
	/// </summary>
	public Instant? FinishedAt { get; init; }

	/// <summary>
	///     When set, the session's expected leaf -- a mismatch is treated identically to a nonexistent
	///     session (<see cref="EntityNotFoundException" />), the same as <see cref="FinishSessionRequest.LeafWorkId" />.
	/// </summary>
	public JobNodeId? LeafWorkId { get; init; }

	/// <summary>
	///     An optional write-up change applied to the leaf's node in the same transaction and commit as
	///     the finish (remediation plan §2.1) -- <see langword="null" /> means no write-up change.
	/// </summary>
	public WriteUpChange? WriteUpChange { get; init; }
}
