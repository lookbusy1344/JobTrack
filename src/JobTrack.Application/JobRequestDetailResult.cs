namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     One request's requester-safe detail projection (ADR 0034, plan §7/§8 <c>/Requests/{id}</c>):
///     status, the read-only subtree with ADR 0054's aggregate allocated duration, and the notes
///     visible to the calling actor. A requester caller sees only requester-visible notes; a
///     staff/admin caller sees every note.
/// </summary>
public sealed record JobRequestDetailResult
{
	/// <summary>The request's anchor <c>job_node</c> identifier.</summary>
	public required JobNodeId JobNodeId { get; init; }

	/// <summary>The client account that submitted the request, distinct from the job node's operational owner.</summary>
	public required AppUserId RequesterUserId { get; init; }

	/// <summary>The requester's current display name.</summary>
	public required string RequesterDisplayName { get; init; }

	/// <summary>The requester's current login username.</summary>
	public required string RequesterUserName { get; init; }

	/// <summary>The anchor node's description.</summary>
	public required string Description { get; init; }

	/// <summary>The request's public status, derived from the whole subtree (ADR 0034).</summary>
	public required RequesterStatus Status { get; init; }

	/// <summary>
	///     The anchor node's structural classification, derived from real relationships at read time
	///     (ADR 0035) — <see cref="NodeKind.Leaf" /> until triage decomposes the request,
	///     <see cref="NodeKind.Branch" /> afterwards. Tells a caller whether
	///     <see cref="LeafAchievement" /> can carry a value at all.
	/// </summary>
	public required NodeKind Kind { get; init; }

	/// <summary>
	///     The rollup achievement over the anchor node's whole subtree (spec §5.2): <see cref="BranchAchievement.Success" />
	///     iff every childless node in it succeeded. Meaningful for a leaf and a branch alike — for a
	///     leaf it collapses that one node's <see cref="LeafAchievement" /> to the same two-value
	///     vocabulary, so a caller can render one rollup field regardless of <see cref="Kind" />.
	/// </summary>
	public required BranchAchievement SubtreeAchievement { get; init; }

	/// <summary>
	///     The anchor node's own recorded achievement when it is a leaf carrying <c>LeafWork</c>;
	///     <see langword="null" /> for a branch (where the six-value leaf vocabulary does not apply) or
	///     for a leaf with no work attached yet. This is the detail <see cref="SubtreeAchievement" />
	///     deliberately collapses away.
	/// </summary>
	public Achievement? LeafAchievement { get; init; }

	/// <summary>
	///     Whether every prerequisite attached to the anchor node or to any of its ancestors is
	///     satisfied (spec §6). Composed by <see cref="IRequestCommands.GetDetailAsync" /> from the
	///     readiness port after the persistence port has performed the authoritative per-request
	///     authorization, the same way <see cref="RequesterSubtreeNodeResult.AllocatedDuration" /> is —
	///     so a persistence port leaves this at its default.
	/// </summary>
	public bool IsReady { get; init; } = true;

	/// <summary>The instant this request was submitted.</summary>
	public required Instant SubmittedAt { get; init; }

	/// <summary>The instant staff acknowledged this request, or <see langword="null" /> if not yet acknowledged.</summary>
	public required Instant? AcknowledgedAt { get; init; }

	/// <summary>The request's optimistic-concurrency version.</summary>
	public required long Version { get; init; }

	/// <summary>The requester-safe subtree rooted at the anchor node, including descendants created by decomposition.</summary>
	public required EquatableArray<RequesterSubtreeNodeResult> Subtree { get; init; }

	/// <summary>The notes visible to the calling actor, oldest first.</summary>
	public required EquatableArray<JobRequestNoteResult> Notes { get; init; }
}
