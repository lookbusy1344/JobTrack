namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     Input to <see cref="IWorkCommands.ReopenAndStartWorkAsync" />: the atomic composite that
///     transitions a terminal leaf back to <see cref="Achievement.Waiting" /> with an audited reason,
///     applies ADR 0038's existing <see cref="Achievement.Waiting" /> -&gt; <see cref="Achievement.InProgress" />
///     auto-advance, and starts <see cref="WorkedByUserId" />'s session, in one commit (ADR 0045 §1/§2).
/// </summary>
public sealed record ReopenAndStartWorkRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The terminal leaf being reopened.</summary>
	public required JobNodeId JobNodeId { get; init; }

	/// <summary>The leaf's expected current (terminal) optimistic-concurrency version.</summary>
	public required long Version { get; init; }

	/// <summary>
	///     Why this terminal state is being reopened. Required and non-blank (ADR 0001's existing
	///     reopening-reason requirement, carried by this composite). The persisted audit value is the
	///     resolved text the caller confirmed, never a UI option code (ADR 0045 §4).
	/// </summary>
	public required string Reason { get; init; }

	/// <summary>
	///     The employee performing the new session's work. Defaults to the actor when the actor's
	///     authority comes only from prior participation on this leaf -- the command rejects naming a
	///     different worker in that case (ADR 0045 §2).
	/// </summary>
	public required AppUserId WorkedByUserId { get; init; }

	/// <summary>
	///     The new session's start instant, or <see langword="null" /> to capture "now". May be a past
	///     instant when recording work which began earlier, but must not be in the future (ADR 0028).
	/// </summary>
	public Instant? StartedAt { get; init; }
}
