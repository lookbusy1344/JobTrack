namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Job-node structural commands (plan §7.3 steps 3–5: create, edit, move, archive, and
///     conditionally delete planning nodes; attach leaf work and decompose a worked leaf atomically;
///     add/remove prerequisites; docs/api/jobtrack-client-design.md).
/// </summary>
public interface IJobCommands
{
	/// <summary>Creates a new child node under an existing parent.</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage the parent node's subtree (see <see cref="Domain.Authorization.JobNodeAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The parent node does not exist.</exception>
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
	///     Atomically decomposes a currently-worked leaf into a branch, a child inheriting the existing
	///     work, and the newly identified children (spec §3.5).
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage this node's subtree (see <see cref="Domain.Authorization.JobNodeAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The leaf node does not exist.</exception>
	/// <exception cref="ConcurrencyConflictException">The supplied version is stale.</exception>
	/// <exception cref="InvariantViolationException">The node has no <c>LeafWork</c> attached to decompose.</exception>
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
	///     prerequisite edges between them — in one transaction (see <see cref="ImportSubtreeRequest" />):
	///     either every node and edge is created, or none is.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage <see cref="ImportSubtreeRequest.ParentId" />'s subtree (see
	///     <see cref="Domain.Authorization.JobNodeAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException"><see cref="ImportSubtreeRequest.ParentId" /> does not exist.</exception>
	/// <exception cref="InvariantViolationException">
	///     The batch is empty, has a duplicate local id, references an unknown parent or prerequisite
	///     local id, its parent references form a cycle, or a prerequisite edge violates spec §6 (self-
	///     referential, ancestor/descendant, duplicate, or would create a cycle).
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
