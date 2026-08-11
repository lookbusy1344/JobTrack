namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     Input to <see cref="IWorkCommands.FinishSessionAsync" />. "Pause" and "stop" are UI descriptions
///     of this same operation, not distinct commands (spec §4.4). When <see cref="FinishedAt" /> is
///     <see langword="null" />, the command captures one clock value ("now") itself (plan §2). A caller
///     may instead supply an explicit past instant to record when the session actually finished — this
///     is a first-time entry of that instant, not a correction, so it carries no reason and no audit
///     "before" value (unlike <see cref="CorrectSessionRequest" />); it must be after the session's start
///     and must not be in the future (ADR 0028).
/// </summary>
public sealed record FinishSessionRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The active session being finished.</summary>
	public required WorkSessionId SessionId { get; init; }

	/// <summary>The caller's expected current optimistic-concurrency version.</summary>
	public required long Version { get; init; }

	/// <summary>
	///     The session's finish instant, or <see langword="null" /> to capture "now". Must be after the
	///     session's start instant and must not be in the future (ADR 0028).
	/// </summary>
	public Instant? FinishedAt { get; init; }

	/// <summary>
	///     When set, the session's expected leaf -- a mismatch is treated identically to a nonexistent
	///     session (<see cref="EntityNotFoundException" />). Lets a caller that reached
	///     <see cref="SessionId" /> through a nested route (remediation plan §3.5, e.g.
	///     <c>/jobs/{nodeId}/sessions/{sessionId}/finish</c>) enforce that the route's parent identifier
	///     actually matches, without a second round-trip. <see langword="null" /> (the default for
	///     in-process callers that already know the session is theirs) skips the check.
	/// </summary>
	public JobNodeId? LeafWorkId { get; init; }
}
