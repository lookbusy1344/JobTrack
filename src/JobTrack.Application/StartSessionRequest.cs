namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     Input to <see cref="IWorkCommands.StartSessionAsync" />. When <see cref="StartedAt" /> is
///     <see langword="null" />, the command captures one clock value ("now") itself (plan §2: "one
///     captured clock value for each operation that depends on current time"). A caller may instead
///     supply an explicit past instant to record a session that already started — this is a first-time
///     entry of that instant, not a correction, so it carries no reason and no audit "before" value
///     (unlike <see cref="CorrectSessionRequest" />); it must not be in the future (ADR 0028). A UI
///     "resume" action is this same command called again, not a distinct operation (spec §4.4).
/// </summary>
public sealed record StartSessionRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The leaf being worked (<c>leaf_work_id</c>, the leaf's <c>job_node_id</c>).</summary>
	public required JobNodeId LeafWorkId { get; init; }

	/// <summary>The employee performing this session's work.</summary>
	public required AppUserId WorkedByUserId { get; init; }

	/// <summary>
	///     The session's start instant, or <see langword="null" /> to capture "now". Must not be in the
	///     future (ADR 0028).
	/// </summary>
	public Instant? StartedAt { get; init; }
}
