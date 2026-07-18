namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     Work already performed against an imported leaf, recorded as part of the same import
///     transaction that creates the node (<see cref="ImportSubtreeNodeSpec.LeafWork" />). This is how a
///     bulk-authoring caller reconstructs history it already knows about — a leaf that has been under
///     way since yesterday, or one that was started and closed last week — rather than creating the
///     tree and then replaying <see cref="IWorkCommands.StartWorkAsync" />/
///     <see cref="IWorkCommands.FinishSessionAsync" />/<see cref="IWorkCommands.SetAchievementAsync" />
///     call by call, which would split one logical write across several transactions.
///     <para>
///         The import attaches <c>LeafWork</c>, records exactly one <c>work_session</c> spanning
///         <see cref="StartedAt" /> to <see cref="FinishedAt" />, and sets the leaf's
///         <see cref="Achievement" /> — all subject to the same prerequisite gate (spec §6) and
///         achievement state machine (ADR 0001) the individual commands enforce.
///     </para>
/// </summary>
public sealed record ImportSubtreeLeafWorkSpec
{
	/// <summary>The employee who performed the session's work.</summary>
	public required AppUserId WorkedByUserId { get; init; }

	/// <summary>
	///     The instant the session started. Must not be in the future relative to the import's own
	///     captured clock value (<c>ConstraintId</c> <c>"work-session-start-in-future"</c>, ADR 0028).
	/// </summary>
	public required Instant StartedAt { get; init; }

	/// <summary>
	///     The instant the session finished, or <see langword="null" /> for a session still under way at
	///     import time. When set it must be strictly after <see cref="StartedAt" /> and must not be in
	///     the future.
	/// </summary>
	public Instant? FinishedAt { get; init; }

	/// <summary>
	///     The achievement the leaf ends the import in. Must be <see cref="Abstractions.Achievement.InProgress" />,
	///     <see cref="Abstractions.Achievement.Success" />, <see cref="Abstractions.Achievement.Cancelled" />, or
	///     <see cref="Abstractions.Achievement.Unsuccessful" /> — <see cref="Abstractions.Achievement.None" /> and
	///     <see cref="Abstractions.Achievement.Waiting" /> both describe a leaf that has had no work, which
	///     contradicts recording a session at all. A terminal achievement additionally requires
	///     <see cref="FinishedAt" />: a leaf cannot be closed while its only session is still open.
	/// </summary>
	public required Achievement Achievement { get; init; }
}
