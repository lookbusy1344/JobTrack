namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     Result of <see cref="IJobQueries.GetLeafWorkPageAsync" /> (unified-leaf-workflow plan Stage 4).
///     Every <c>Can*</c> member is a rendering hint only (same convention as
///     <see cref="LeafSessionManageCapabilityResult" />) -- <see cref="IWorkCommands.CompleteLeafAsync" />
///     and <see cref="IWorkCommands.ReopenAndStartWorkAsync" /> each reload and reauthorize inside their
///     own transaction regardless of what this projection showed.
/// </summary>
public sealed record LeafWorkPageResult
{
	/// <summary>The leaf this page describes.</summary>
	public required JobNodeId JobNodeId { get; init; }

	/// <summary>The node's description.</summary>
	public required string Description { get; init; }

	/// <summary>The node's direct owner, or <see langword="null" /> when unassigned.</summary>
	public AppUserId? OwnerUserId { get; init; }

	/// <summary>When the node was archived, or <see langword="null" /> if it is not.</summary>
	public Instant? ArchivedAt { get; init; }

	/// <summary>Whether this leaf has <c>LeafWork</c> attached at all.</summary>
	public required bool HasLeafWork { get; init; }

	/// <summary>The leaf's current achievement, when <see cref="HasLeafWork" />.</summary>
	public Achievement? Achievement { get; init; }

	/// <summary>The <c>LeafWork</c>'s optimistic-concurrency version, when <see cref="HasLeafWork" />.</summary>
	public long? LeafWorkVersion { get; init; }

	/// <summary>Whether every prerequisite attached to this leaf or an ancestor is currently satisfied.</summary>
	public required bool IsReady { get; init; }

	/// <summary>Every currently active session on this leaf, never collapsed to one representative (ADR 0041).</summary>
	public required EquatableArray<WorkSessionResult> ActiveSessions { get; init; }

	/// <summary>Whether the actor controls this node -- owns it directly or inherits control from an owned ancestor.</summary>
	public required bool ActorControlsNode { get; init; }

	/// <summary>Whether the actor has recorded any previous session on this leaf (ADR 0045 §2's prior-participation authority).</summary>
	public required bool ActorParticipatedPreviously { get; init; }

	/// <summary>Whether the actor may currently manage (start/finish on behalf of another worker, or their own) sessions on this leaf.</summary>
	public required bool CanManageSessions { get; init; }

	/// <summary>Whether the actor holds <see cref="IWorkCommands.CompleteLeafAsync" />'s required authority (ADR 0045 §3.6).</summary>
	public required bool CanComplete { get; init; }

	/// <summary>Whether the actor may reopen this terminal leaf and start a session for themselves (ADR 0045 §2).</summary>
	public required bool CanReopenAndStartForSelf { get; init; }

	/// <summary>Whether the actor may reopen this terminal leaf and start a session for any eligible target worker (ADR 0045 §2).</summary>
	public required bool CanReopenAndStartForOthers { get; init; }

	/// <summary>Whether the actor may perform the elevated terminal-to-Waiting correction without starting a session.</summary>
	public required bool CanReopenWithoutStarting { get; init; }

	/// <summary>
	///     The count of jobs that directly require this leaf as a prerequisite -- non-zero here means
	///     reopening a currently-<see cref="Abstractions.Achievement.Success" /> leaf would regress
	///     readiness for at least one dependent (ADR 0045 §2's dependent-impact warning).
	/// </summary>
	public required int DirectDependentCount { get; init; }
}
