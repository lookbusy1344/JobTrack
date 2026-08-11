namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     Input to <see cref="IWorkCommands.CorrectSessionAsync" /> (spec §4.4). Every historical
///     correction requires a reason and produces an audit record of the previous and replacement
///     values; no second-person approval is required.
/// </summary>
public sealed record CorrectSessionRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The session being corrected.</summary>
	public required WorkSessionId SessionId { get; init; }

	/// <summary>The corrected start instant.</summary>
	public required Instant StartedAt { get; init; }

	/// <summary>The corrected finish instant, or null to leave the session active.</summary>
	public Instant? FinishedAt { get; init; }

	/// <summary>Why this correction is being made.</summary>
	public required string Reason { get; init; }

	/// <summary>The caller's expected current optimistic-concurrency version.</summary>
	public required long Version { get; init; }

	/// <summary>
	///     When set, the session's expected leaf -- a mismatch is treated identically to a nonexistent
	///     session (<see cref="EntityNotFoundException" />). Lets a caller that reached
	///     <see cref="SessionId" /> through a nested route (remediation plan §3.5, e.g.
	///     <c>/jobs/{nodeId}/sessions/{sessionId}/correct</c>) enforce that the route's parent identifier
	///     actually matches, without a second round-trip. <see langword="null" /> (the default for
	///     in-process callers that already know the session is theirs) skips the check.
	/// </summary>
	public JobNodeId? LeafWorkId { get; init; }
}
