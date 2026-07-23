namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     Input to <see cref="IWorkCommands.CompleteLeafAsync" />: the atomic composite that finishes an
///     exact, caller-confirmed active-session set and transitions the leaf
///     <see cref="Achievement.InProgress" /> -&gt; <see cref="FinalAchievement" />, in one commit (ADR
///     0045 §1/§3, ADR 0047). <see cref="ExpectedActiveSessions" /> may be empty -- a previously paused
///     <see cref="Achievement.InProgress" /> leaf with no active session can be completed without
///     fabricating one.
/// </summary>
public sealed record CompleteLeafRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The leaf being completed.</summary>
	public required JobNodeId JobNodeId { get; init; }

	/// <summary>The leaf's expected current optimistic-concurrency version.</summary>
	public required long Version { get; init; }

	/// <summary>
	///     The exact active sessions the caller reviewed and confirmed for this completion, by id and
	///     version. The command re-verifies this is exactly the leaf's current active-session set before
	///     finishing any of them (ADR 0045 §3); a session that started or finished concurrently produces
	///     a conflict rather than being silently included or excluded.
	/// </summary>
	public required EquatableArray<ExpectedActiveSession> ExpectedActiveSessions { get; init; }

	/// <summary>
	///     The achievement recorded once every session in <see cref="ExpectedActiveSessions" /> has
	///     finished (ADR 0047) -- <see cref="Achievement.Success" /> by default, or
	///     <see cref="Achievement.Cancelled" />/<see cref="Achievement.Unsuccessful" />, the only other
	///     values <see cref="Domain.Hierarchy.AchievementTransitions.IsPermitted" /> allows from
	///     <see cref="Achievement.InProgress" />. Any other value throws <see cref="InvariantViolationException" />
	///     with <c>ConstraintId</c> <c>"achievement-transition-not-permitted"</c>, the same as
	///     <see cref="IWorkCommands.SetAchievementAsync" />.
	/// </summary>
	public Achievement FinalAchievement { get; init; } = Achievement.Success;

	/// <summary>
	///     The one finish instant applied to every session in <see cref="ExpectedActiveSessions" />, or
	///     <see langword="null" /> to capture "now". Must be later than every affected session's start
	///     instant and must not be in the future (ADR 0028).
	/// </summary>
	public Instant? FinishedAt { get; init; }

	/// <summary>
	///     An optional free-text note appended to the fixed structured completion reason (ADR 0045 §4,
	///     e.g. "Completed from the leaf work page"). Never a substitute for that structured reason and
	///     never required.
	/// </summary>
	public string? CompletionNote { get; init; }
}
