namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     Input to <see cref="IWorkCommands.PauseLeafAsync" />: the atomic composite that finishes an
///     exact, caller-confirmed active-session set at one instant and leaves the leaf's achievement
///     untouched, in one commit. The pause counterpart of <see cref="CompleteLeafRequest" /> -- pausing
///     a leaf several workers are clocked onto stops all of them, so a leaf never sits half-paused with
///     one worker's clock still running. <see cref="ExpectedActiveSessions" /> may be empty, which
///     leaves only a <see cref="WriteUpChange" /> (if any) to apply.
/// </summary>
public sealed record PauseLeafRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The leaf being paused.</summary>
	public required JobNodeId JobNodeId { get; init; }

	/// <summary>
	///     The exact active sessions the caller reviewed and confirmed for this pause, by id and
	///     version. The command re-verifies this is exactly the leaf's current active-session set before
	///     finishing any of them (the same rule <see cref="CompleteLeafRequest.ExpectedActiveSessions" />
	///     follows); a session that started or finished concurrently produces a conflict rather than
	///     being silently included or excluded.
	/// </summary>
	public required EquatableArray<ExpectedActiveSession> ExpectedActiveSessions { get; init; }

	/// <summary>
	///     The one finish instant applied to every session in <see cref="ExpectedActiveSessions" />, or
	///     <see langword="null" /> to capture "now". Must be later than every affected session's start
	///     instant and must not be in the future (ADR 0028).
	/// </summary>
	public Instant? FinishedAt { get; init; }

	/// <summary>
	///     An optional write-up change applied to the leaf's node in the same transaction and commit as
	///     this pause -- <see langword="null" /> means no write-up change.
	/// </summary>
	public WriteUpChange? WriteUpChange { get; init; }
}
