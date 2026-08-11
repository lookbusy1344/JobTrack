namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     One session the caller confirmed as part of the exact active-session set being finished by
///     <see cref="IWorkCommands.CompleteLeafAsync" /> (ADR 0045 §1/§3). The command re-verifies that the
///     leaf's currently active sessions are exactly this set, by id and version, before finishing any of
///     them -- a session that started concurrently after the caller's read, or one that finished
///     concurrently, must produce a conflict rather than being silently swept into, or excluded from,
///     the finish.
/// </summary>
public sealed record ExpectedActiveSession
{
	/// <summary>The active session's identifier.</summary>
	public required WorkSessionId Id { get; init; }

	/// <summary>The session's expected current optimistic-concurrency version.</summary>
	public required long Version { get; init; }
}
