namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Job-node structural commands (plan §7.3 steps 3–5: create, edit, move, archive, and
///     conditionally delete planning nodes; attach leaf work and decompose a worked leaf atomically;
///     add/remove prerequisites; docs/api/jobtrack-client-design.md).
/// </summary>
public interface IJobCommands
{
	/// <summary>
	///     Creates a new child node under an existing parent. A supplied
	///     <see cref="CreateJobNodeRequest.BeginWork" /> additionally attaches <c>LeafWork</c>, advances it
	///     to <see cref="Achievement.InProgress" />, and opens the named worker's session -- all in the
	///     same transaction, so a node is never created and left half-started.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage the parent node's subtree (see <see cref="Domain.Authorization.JobNodeAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The parent node does not exist.</exception>
	/// <exception cref="InvariantViolationException">
	///     <see cref="CreateJobNodeRequest.OwnerUserId" /> names an owner who is disabled, locked, or holds
	///     no eligible workflow role (<c>ConstraintId</c> <c>"job-node-owner-not-eligible"</c>); or, with a
	///     supplied <see cref="CreateJobNodeRequest.BeginWork" />, its <c>WorkedByUserId</c> names such a
	///     worker (<c>ConstraintId</c> <c>"work-session-target-not-eligible"</c>, ADR 0044 Stage 6) or the
	///     parent already holds <c>LeafWork</c> of its own (<c>ConstraintId</c>
	///     <c>"job-node-parent-has-no-leaf-work"</c>).
	/// </exception>
	/// <exception cref="PrerequisiteBlockedException">
	///     <see cref="CreateJobNodeRequest.BeginWork" /> was supplied and the new node's inherited
	///     prerequisites are not satisfied (spec §6) -- work cannot begin on a leaf that is blocked the
	///     instant it exists.
	/// </exception>
	Task<JobNodeResult> AddChildAsync(CreateJobNodeRequest request, CancellationToken cancellationToken = default);

	/// <summary>Replaces a node's editable fields.</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage this node's subtree (see <see cref="Domain.Authorization.JobNodeAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The node does not exist.</exception>
	/// <exception cref="ConcurrencyConflictException">The supplied version is stale.</exception>
	Task<JobNodeResult> EditAsync(EditJobNodeRequest request, CancellationToken cancellationToken = default);

	/// <summary>Re-parents a node.</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage the node being moved (see
	///     <see cref="Domain.Authorization.JobNodeAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The node or the destination parent does not exist.</exception>
	/// <exception cref="ConcurrencyConflictException">The supplied version is stale.</exception>
	/// <exception cref="InvariantViolationException">The move would create a hierarchy cycle.</exception>
	Task<JobNodeResult> MoveAsync(MoveJobNodeRequest request, CancellationToken cancellationToken = default);

	/// <summary>Archives a node, removing it from default operational views without deleting it.</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage this node's subtree (see <see cref="Domain.Authorization.JobNodeAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The node does not exist.</exception>
	/// <exception cref="ConcurrencyConflictException">The supplied version is stale.</exception>
	/// <exception cref="InvariantViolationException">
	///     A session on this node's <c>LeafWork</c> is still active (<c>ConstraintId</c>
	///     <c>"leaf-closure-active-sessions"</c>, ADR 0044).
	/// </exception>
	Task<JobNodeResult> ArchiveAsync(ArchiveJobNodeRequest request, CancellationToken cancellationToken = default);

	/// <summary>Physically deletes a proven-unused planning node.</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage this node's subtree (see <see cref="Domain.Authorization.JobNodeAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The node does not exist.</exception>
	/// <exception cref="ConcurrencyConflictException">The supplied version is stale.</exception>
	/// <exception cref="InvariantViolationException">
	///     The node has dependent data (<c>LeafWork</c>, a <c>WorkSession</c>, a completed descendant,
	///     or cost-relevant/audit history) and cannot be physically deleted (spec §3.6).
	/// </exception>
	Task DeleteAsync(DeleteJobNodeRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Recursively and permanently destroys a subtree — the root, every descendant, and their
	///     <c>LeafWork</c>, <c>WorkSession</c>, rate-override, request, and prerequisite rows — in one
	///     transaction (ADR 0061, superseding ADR 0036's prohibition). Every prerequisite edge touching
	///     the subtree is dropped, including edges arriving from outside, so an external dependent can
	///     become ready where it was blocked. Destroyed session history stops counting toward every
	///     surviving ancestor's cost, which is an accepted and irreversible consequence.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor does not hold <see cref="EmployeeRole.Administrator" />
	///     (<see cref="Domain.Authorization.JobNodeDeletePolicy.CanDeleteSubtree" />), or may not manage
	///     this node's subtree.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The root node does not exist.</exception>
	/// <exception cref="ConcurrencyConflictException">The supplied version is stale.</exception>
	/// <exception cref="InvariantViolationException">
	///     The root is the permanent root (<c>"job-node-is-root-cannot-delete"</c>), a request holding
	///     area is anchored inside the subtree (<c>"subtree-delete-holding-area-anchored"</c>), or
	///     <see cref="DeleteSubtreeRequest.Reason" /> is blank
	///     (<c>"subtree-delete-reason-required"</c>).
	/// </exception>
	Task<DeleteSubtreeResult> DeleteSubtreeAsync(DeleteSubtreeRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Archives a subtree root and every descendant not already archived, in one transaction — the
	///     non-destructive alternative to <see cref="DeleteSubtreeAsync" /> (ADR 0061). Nodes already
	///     archived keep their original instant.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor does not hold <see cref="EmployeeRole.Administrator" />, or may not manage this
	///     node's subtree.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The root node does not exist.</exception>
	/// <exception cref="ConcurrencyConflictException">The supplied version is stale.</exception>
	/// <exception cref="InvariantViolationException">
	///     A session on some leaf in the subtree is still active (<c>ConstraintId</c>
	///     <c>"leaf-closure-active-sessions"</c>, ADR 0044).
	/// </exception>
	Task<ArchiveSubtreeResult> ArchiveSubtreeAsync(ArchiveSubtreeRequest request, CancellationToken cancellationToken = default);

	/// <summary>Attaches achievement tracking to an existing bare leaf node.</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage this node's subtree (see <see cref="Domain.Authorization.JobNodeAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The leaf node does not exist.</exception>
	/// <exception cref="InvariantViolationException">
	///     The node already has children, or already has <c>LeafWork</c> attached (leaf/branch exclusivity, spec §4.2).
	/// </exception>
	Task<LeafWorkResult> AttachLeafWorkAsync(AttachLeafWorkRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Atomically decomposes a leaf into a branch and the newly identified children (spec §3.5). If
	///     the leaf currently has <c>LeafWork</c> attached, the existing work also becomes its own child
	///     first; a bare leaf with no <c>LeafWork</c> simply becomes a branch of the named children
	///     (ADR 0067).
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage this node's subtree (see <see cref="Domain.Authorization.JobNodeAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The leaf node does not exist.</exception>
	/// <exception cref="ConcurrencyConflictException">The supplied version is stale.</exception>
	/// <exception cref="InvariantViolationException">
	///     The node is the root (<c>ConstraintId</c> <c>"job-node-is-root-cannot-decompose"</c>), already
	///     has children (<c>"job-node-has-children-cannot-decompose"</c>), or is a bare leaf with no
	///     named new children (<c>"job-node-decompose-requires-a-child"</c>).
	/// </exception>
	Task<DecomposeWorkedLeafResult> DecomposeWorkedLeafAsync(
		DecomposeWorkedLeafRequest request, CancellationToken cancellationToken = default);

	/// <summary>Adds a prerequisite edge: <c>RequiredJobId</c> must succeed before <c>DependentJobId</c> is ready.</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage the required or dependent job's subtree (see
	///     <see cref="Domain.Authorization.JobNodeAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">Either job does not exist.</exception>
	/// <exception cref="InvariantViolationException">
	///     The edge is self-referential, would create a prerequisite cycle, would duplicate an existing
	///     edge, or its endpoints are already ancestor/descendant in the job hierarchy (spec §6).
	/// </exception>
	Task AddPrerequisiteAsync(AddPrerequisiteRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Atomic composite: adds every edge in <see cref="AddPrerequisitesRequest.Edges" /> in one
	///     provider transaction and correlation. If any edge is invalid or unauthorized, no edge is
	///     committed.
	/// </summary>
	Task AddPrerequisitesAsync(AddPrerequisitesRequest request, CancellationToken cancellationToken = default);

	/// <summary>Removes a prerequisite edge.</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage the required or dependent job's subtree (see
	///     <see cref="Domain.Authorization.JobNodeAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">Either job, or the edge itself, does not exist.</exception>
	Task RemovePrerequisiteAsync(RemovePrerequisiteRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Atomically creates a whole batch of new child nodes — a subtree of any shape, plus
	///     prerequisite edges between them, and optionally the home-node assignments named by
	///     <see cref="ImportSubtreeRequest.HomeNodeLocalId" />/<see cref="ImportSubtreeRequest.HomeNodeUserIds" />
	///     — in one transaction (see <see cref="ImportSubtreeRequest" />): either every node, edge, and
	///     home-node assignment is written, or none is.
	/// </summary>
	/// <exception cref="ArgumentException">
	///     <see cref="ImportSubtreeRequest.HomeNodeLocalId" /> names no node in the batch, or
	///     <see cref="ImportSubtreeRequest.HomeNodeUserIds" /> is non-empty without one or contains a
	///     duplicate account.
	/// </exception>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage <see cref="ImportSubtreeRequest.ParentId" />'s subtree (see
	///     <see cref="Domain.Authorization.JobNodeAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException"><see cref="ImportSubtreeRequest.ParentId" /> does not exist.</exception>
	/// <exception cref="InvariantViolationException">
	///     The batch is empty, has a duplicate local id, references an unknown parent or prerequisite
	///     local id, its parent references form a cycle, a prerequisite edge violates spec §6 (self-
	///     referential, ancestor/descendant, duplicate, or would create a cycle), or the flagged home
	///     node imports as a leaf (<c>ConstraintId</c> <c>"home-node-must-not-be-leaf"</c>), or a target
	///     account is disabled or locked (<c>ConstraintId</c> <c>"home-node-target-not-active"</c>).
	/// </exception>
	Task<ImportSubtreeResult> ImportSubtreeAsync(ImportSubtreeRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Claims an unassigned node from the pickup pool (ownership model §4.3), setting its direct
	///     owner to the acting user. Claiming a branch grants the claimant control over its entire
	///     subtree through the ordinary ancestor rule, including already-owned descendants.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor holds none of Worker, JobManager, or Administrator (see
	///     <see cref="Domain.Authorization.JobPickupPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The node does not exist.</exception>
	/// <exception cref="InvariantViolationException">
	///     The node is already owned — by another claimant's concurrent pickup, or because it was never
	///     unassigned to begin with.
	/// </exception>
	Task<JobNodeResult> PickUpAsync(PickUpJobNodeRequest request, CancellationToken cancellationToken = default);
}
