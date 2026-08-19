namespace JobTrack.Persistence.Sqlite;

using System.Data;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using Domain.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared;
using Shared.Entities;

/// <summary>
///     SQLite implementation of <see cref="IJobNodeCommandPort" /> (impl plan §7.3 slices 3-5: create,
///     edit, move, archive, and conditionally delete planning nodes; attach leaf work and decompose a
///     worked leaf; add/remove prerequisites). One <see cref="SqliteJobTrackDbContext" />/connection/
///     transaction per call; SQLite has no advisory lock or stored function, so
///     <see cref="IsolationLevel.Serializable" /> starts a <c>BEGIN IMMEDIATE</c> transaction that
///     serializes concurrent writes through SQLite's single-writer model (matches
///     <see cref="SqliteInstallationBootstrapPort" />'s established use of the same technique).
/// </summary>
internal sealed partial class SqliteJobNodeCommandPort : IJobNodeCommandPort
{
	/// <summary>
	///     SQLite's <c>SQLITE_CONSTRAINT</c> primary result code (sqlite3.h): the base code
	///     shared by <c>job_node_no_cycle</c>'s <c>RAISE(ABORT, ...)</c> and the self-parent/root-guard
	///     checks, distinguishing them from transient errors (e.g. <c>SQLITE_BUSY</c>) that must not be
	///     misreported as a cycle violation.
	/// </summary>
	private const int SqliteConstraintErrorCode = 19;

	/// <summary>
	///     ADR 0044: the literal message <c>job_node_no_active_sessions_on_archive</c> (schema version
	///     0007) raises via <c>RAISE(ABORT, ...)</c>.
	/// </summary>
	private const string ActiveSessionsMessage = "leaf-closure-active-sessions";

	/// <summary>
	///     Spec §6 rule 5's move side: the literal message
	///     <c>job_prerequisite_edges_after_move</c> (schema version 0008) raises via
	///     <c>RAISE(ABORT, ...)</c>. Matched ahead of the generic constraint catch, which would
	///     otherwise report this rejection as a hierarchy cycle.
	/// </summary>
	private const string PrerequisiteEdgeAfterMoveMessage = "job-node-move-would-invalidate-prerequisite";

	private readonly IClock clock;

	private readonly string connectionString;

	/// <summary>Creates the port over the given SQLite connection string.</summary>
	public SqliteJobNodeCommandPort(string connectionString, IClock clock)
	{
		this.connectionString = connectionString;
		this.clock = clock;
	}

	/// <inheritdoc />
	public Task<JobNodeResult> AddChildAsync(CreateJobNodeRequest request, CancellationToken cancellationToken = default) =>
		CreateAsync(request, cancellationToken);

	/// <inheritdoc />
	public async Task<JobNodeResult> EditAsync(EditJobNodeRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
												   .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		var node = await LoadTrackedNodeAsync(context, request.NodeId, cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.NodeId, now, cancellationToken).ConfigureAwait(false);
		CheckVersionOrThrow(node.RowVersion, request.Version);
		EnsureRootOwnerNotNulledOrThrow(node, request.OwnerUserId);
		await WorkflowEmployeeEligibility.EnsureMayBeAssignedWorkAsync(
			context, request.OwnerUserId, now, "job-node-owner-not-eligible", cancellationToken).ConfigureAwait(false);

		var before = SnapshotJobNode(node);

		node.Description = request.Description;
		node.WriteUp = request.WriteUp;
		node.OwnerUserId = request.OwnerUserId;
		node.ExpectedDurationHours = request.ExpectedDurationHours;
		node.ExpectedCost = request.ExpectedCost;
		node.NeededStart = request.NeededStart;
		node.NeededFinish = request.NeededFinish;
		node.Priority = request.Priority;
		node.RowVersion += 1;

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "edit-job-node", "job_node", node.Id.Value,
			request.Context.CorrelationId, null, before, SnapshotJobNode(node));

		await JobNodeWriteExceptionTranslation.SaveChangesAndCommitAsync(context, transaction, cancellationToken).ConfigureAwait(false);

		return await JobNodeStructuralProjection.ToResultAsync(context, node, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task<JobNodeResult> MoveAsync(MoveJobNodeRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
												   .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		var actorRoles = await GetActorRolesAsync(context, request.Context.Actor, now, cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, actorRoles, request.Context.Actor, request.NodeId, cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, actorRoles, request.Context.Actor, request.NewParentId, cancellationToken).ConfigureAwait(false);

		var oldParentId = await context.Set<JobNodeEntity>().AsNoTracking()
									   .Where(n => n.Id == request.NodeId).Select(n => n.ParentId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

		int affected;
		try {
			// job_node_no_cycle (schema version 0005) and the self-parent CHECK constraint
			// (schema version 0004) fire immediately from this UPDATE -- SQLite has no deferred
			// constraint triggers (impl plan §7.4).
			affected = await context.Set<JobNodeEntity>()
									.Where(n => n.Id == request.NodeId && n.RowVersion == request.Version)
									.ExecuteUpdateAsync(
										setters => setters
												   .SetProperty(n => n.ParentId, request.NewParentId)
												   .SetProperty(n => n.RowVersion, n => n.RowVersion + 1),
										cancellationToken)
									.ConfigureAwait(false);
		}
		catch (SqliteException ex) when (ex.Message.Contains(PrerequisiteEdgeAfterMoveMessage, StringComparison.Ordinal)) {
			throw new InvariantViolationException(
				"job-node-move-would-invalidate-prerequisite",
				"Moving this node would leave a prerequisite edge connecting an ancestor and a descendant; remove the edge first.",
				ex);
		}
		catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraintErrorCode) {
			throw new InvariantViolationException(
				"job-node-move-would-cycle", "Moving this node under the requested parent would create a cycle.", ex);
		}
		catch (SqliteException ex) {
			throw new InvariantViolationException("job-node-move-invalid", "This move violates a job-node structural invariant.", ex);
		}

		if (affected == 0) {
			throw new ConcurrencyConflictException(
				$"Expected version {request.Version} for job node {request.NodeId} did not match its current version.");
		}

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "move-job-node", "job_node", request.NodeId.Value,
			request.Context.CorrelationId, null,
			new Dictionary<string, string?> {
				["parent_id"] = oldParentId?.Value.ToString(CultureInfo.InvariantCulture),
			},
			new Dictionary<string, string?> {
				["parent_id"] = request.NewParentId.Value.ToString(CultureInfo.InvariantCulture),
			});
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		var moved = await context.Set<JobNodeEntity>().AsNoTracking()
								 .FirstOrDefaultAsync(n => n.Id == request.NodeId, cancellationToken).ConfigureAwait(false)
					?? throw new EntityNotFoundException($"Job node {request.NodeId} no longer exists after the move committed.");

		return await JobNodeStructuralProjection.ToResultAsync(context, moved, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task<JobNodeResult> PickUpAsync(PickUpJobNodeRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
												   .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var before = await context.Set<JobNodeEntity>().AsNoTracking()
								  .FirstOrDefaultAsync(n => n.Id == request.NodeId, cancellationToken).ConfigureAwait(false)
					 ?? throw new EntityNotFoundException($"Job node {request.NodeId} does not exist.");

		var now = clock.GetCurrentInstant();
		var actorRoles = await GetActorRolesAsync(context, request.Context.Actor, now, cancellationToken).ConfigureAwait(false);
		if (!JobPickupPolicy.CanPickUp(actorRoles, true)) {
			throw new AuthorizationDeniedException($"Actor {request.Context.Actor} may not pick up job node {request.NodeId}.");
		}

		// SQLite's BEGIN IMMEDIATE (started above) serializes concurrent writes, so a concurrent
		// claimant that commits first leaves zero rows affected inside UnassignedNodeClaim.
		if (!await UnassignedNodeClaim.TryClaimAsync(context, request.NodeId, request.Context.Actor, cancellationToken)
									  .ConfigureAwait(false)) {
			throw new InvariantViolationException(
				"job-node-already-claimed", $"Job node {request.NodeId} has already been claimed.");
		}

		var claimed = await context.Set<JobNodeEntity>().AsNoTracking()
								   .FirstAsync(n => n.Id == request.NodeId, cancellationToken).ConfigureAwait(false);

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "pick-up-job-node", "job_node", request.NodeId.Value,
			request.Context.CorrelationId, null, SnapshotJobNode(before), SnapshotJobNode(claimed));
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		return await JobNodeStructuralProjection.ToResultAsync(context, claimed, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task<JobNodeResult> ArchiveAsync(ArchiveJobNodeRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
												   .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		var node = await LoadTrackedNodeAsync(context, request.NodeId, cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.NodeId, now, cancellationToken).ConfigureAwait(false);
		CheckVersionOrThrow(node.RowVersion, request.Version);

		// ADR 0044: rejected while any session on this node's LeafWork (if it has one) is still
		// active; the immediate trigger below is the race backstop.
		if (await LeafSessionClosure.HasActiveSessionAsync(context, request.NodeId, cancellationToken).ConfigureAwait(false)) {
			throw new InvariantViolationException(
				"leaf-closure-active-sessions", "This leaf cannot be archived while a session is active on it.");
		}

		var wasArchivedAt = node.ArchivedAt;
		node.ArchivedAt = now;
		node.RowVersion += 1;

		AuditEventWriter.Add(
			context, request.Context.Actor, node.ArchivedAt.Value, "archive-job-node", "job_node", node.Id.Value,
			request.Context.CorrelationId, null,
			new Dictionary<string, string?> {
				["archived_at"] = wasArchivedAt?.ToString(),
			},
			new Dictionary<string, string?> {
				["archived_at"] = node.ArchivedAt?.ToString(),
			});

		try {
			await JobNodeWriteExceptionTranslation.SaveChangesAndCommitAsync(context, transaction, cancellationToken).ConfigureAwait(false);
		}
		catch (InvariantViolationException ex) when (FindActiveSessionsViolation(ex.InnerException) is not null) {
			throw new InvariantViolationException(
				"leaf-closure-active-sessions", "This leaf cannot be archived while a session is active on it.", ex.InnerException!);
		}

		return await JobNodeStructuralProjection.ToResultAsync(context, node, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task DeleteAsync(DeleteJobNodeRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
												   .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		var node = await LoadTrackedNodeAsync(context, request.NodeId, cancellationToken).ConfigureAwait(false);
		var actorRoles = await GetActorRolesAsync(context, request.Context.Actor, now, cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, actorRoles, request.Context.Actor, request.NodeId, cancellationToken).ConfigureAwait(false);
		CheckVersionOrThrow(node.RowVersion, request.Version);

		if (node.ParentId is null) {
			throw new InvariantViolationException("job-node-is-root-cannot-delete", "The root job node cannot be deleted.");
		}

		if (await context.Set<JobNodeEntity>().AsNoTracking()
						 .AnyAsync(c => c.ParentId == request.NodeId, cancellationToken).ConfigureAwait(false)) {
			throw new InvariantViolationException(
				"job-node-has-children-cannot-delete",
				"A node with children cannot be deleted; delete or move its children first.");
		}

		if (await context.Set<JobPrerequisiteEntity>().AsNoTracking()
						 .AnyAsync(jp => jp.FromId == request.NodeId || jp.ToId == request.NodeId, cancellationToken).ConfigureAwait(false)) {
			throw new InvariantViolationException(
				"job-node-has-prerequisites-cannot-delete",
				"A node with a prerequisite edge cannot be deleted; remove the edge(s) first.");
		}

		var leafWork = await context.Set<LeafWorkEntity>()
									.FirstOrDefaultAsync(lw => lw.JobNodeId == request.NodeId, cancellationToken).ConfigureAwait(false);

		Dictionary<string, string?> before;
		string operation;
		string? reason = null;

		if (leafWork is null) {
			before = SnapshotJobNode(node);
			operation = "delete-job-node";
		} else {
			var sessions = await context.Set<WorkSessionEntity>()
										.Where(s => s.LeafWorkId == request.NodeId).ToListAsync(cancellationToken).ConfigureAwait(false);

			if (sessions.Count == 0) {
				before = SnapshotJobNode(node);
				operation = "delete-job-node";
				_ = context.Remove(leafWork);
			} else {
				if (!JobNodeDeletePolicy.CanForceDeleteWorkedLeaf(actorRoles)) {
					throw new AuthorizationDeniedException(
						$"Actor {request.Context.Actor} may not delete job node {request.NodeId}: it has worked session " +
						"history and deletion requires the Administrator role (ADR 0036).");
				}

				if (string.IsNullOrWhiteSpace(request.Reason)) {
					throw new InvariantViolationException(
						"job-node-delete-worked-leaf-reason-required",
						"Deleting a leaf with worked session history requires a reason.");
				}

				before = SnapshotWorkedLeaf(node, leafWork, sessions);
				operation = "delete-worked-leaf";
				reason = request.Reason;
				context.RemoveRange(sessions);
				_ = context.Remove(leafWork);
			}
		}

		foreach (var (key, value) in await JobNodeDependentCascade
										   .RemoveDependentsOfAsync(context, request.NodeId, cancellationToken).ConfigureAwait(false)) {
			before[key] = value;
		}

		AuditEventWriter.Add(
			context, request.Context.Actor, now, operation, "job_node", node.Id.Value,
			request.Context.CorrelationId, reason, before, null);

		_ = context.Remove(node);

		await JobNodeWriteExceptionTranslation.SaveChangesAndCommitForDeleteAsync(context, transaction, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task<DeleteSubtreeResult> DeleteSubtreeAsync(DeleteSubtreeRequest request, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(request.Reason)) {
			throw new InvariantViolationException(
				"subtree-delete-reason-required", "Deleting a subtree requires a reason.");
		}

		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
												   .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		var node = await LoadTrackedNodeAsync(context, request.RootId, cancellationToken).ConfigureAwait(false);
		var actorRoles = await GetActorRolesAsync(context, request.Context.Actor, now, cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, actorRoles, request.Context.Actor, request.RootId, cancellationToken).ConfigureAwait(false);

		if (!JobNodeDeletePolicy.CanDeleteSubtree(actorRoles)) {
			throw new AuthorizationDeniedException(
				$"Actor {request.Context.Actor} may not delete subtree {request.RootId}: recursive deletion " +
				"requires the Administrator role (ADR 0061).");
		}

		CheckVersionOrThrow(node.RowVersion, request.Version);

		if (node.ParentId is null) {
			throw new InvariantViolationException("job-node-is-root-cannot-delete", "The root job node cannot be deleted.");
		}

		// Recomputed here rather than trusted from whatever the confirmation screen measured earlier,
		// so the audit record and the cascade describe the subtree as it is inside this transaction.
		var impact = await SubtreeImpactComputation.ComputeAsync(context, request.RootId, cancellationToken).ConfigureAwait(false);
		if (impact.BlockingHoldingAreas.Count > 0) {
			throw new InvariantViolationException(
				"subtree-delete-holding-area-anchored",
				"A request holding area is anchored inside this subtree; re-anchor or deactivate it first: " +
				string.Join(", ", impact.BlockingHoldingAreas.Select(h => h.Name)));
		}

		return await JobNodeWriteExceptionTranslation.RunAndCommitAsync(
			transaction,
			async ct => {
				// Written before the rows go, since nothing else will survive to describe them.
				AuditEventWriter.Add(
					context, request.Context.Actor, now, "delete-subtree", "job_node", node.Id.Value,
					request.Context.CorrelationId, request.Reason, SubtreeAuditSnapshot.Create(impact), null);
				_ = await context.SaveChangesAsync(ct).ConfigureAwait(false);

				var edgesDropped = await SubtreeDeletionCascade.ExecuteAsync(
					context, impact, DeleteWorkSessionsForLeafWorkAsync, ct).ConfigureAwait(false);

				// The root goes through the tracked entity so its row_version concurrency token is
				// checked: a concurrent deleter that already removed this subtree makes this affect
				// zero rows, surfacing as ConcurrencyConflictException instead of a phantom success.
				_ = context.Remove(node);
				_ = await context.SaveChangesAsync(ct).ConfigureAwait(false);

				return new DeleteSubtreeResult {
					NodeCount = impact.NodeCount,
					LeafWorkCount = impact.LeafWorkCount,
					WorkSessionCount = impact.WorkSessionCount,
					TotalWorkedDuration = impact.TotalWorkedDuration,
					PrerequisiteEdgeCount = edgesDropped,
					JobRequestCount = impact.JobRequestCount,
				};
			},
			cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task<ArchiveSubtreeResult> ArchiveSubtreeAsync(ArchiveSubtreeRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
												   .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		var root = await LoadTrackedNodeAsync(context, request.RootId, cancellationToken).ConfigureAwait(false);
		var actorRoles = await GetActorRolesAsync(context, request.Context.Actor, now, cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, actorRoles, request.Context.Actor, request.RootId, cancellationToken).ConfigureAwait(false);

		if (!JobNodeDeletePolicy.CanDeleteSubtree(actorRoles)) {
			throw new AuthorizationDeniedException(
				$"Actor {request.Context.Actor} may not archive subtree {request.RootId}: recursive archiving " +
				"requires the Administrator role (ADR 0061).");
		}

		CheckVersionOrThrow(root.RowVersion, request.Version);

		var rows = await JobNodeHierarchyQueries.GetSubtreeImpactRowsAsync(context, request.RootId.Value, cancellationToken)
												.ConfigureAwait(false);
		var subtreeIds = rows.Select(r => new JobNodeId(r.Id)).ToList();
		var leafWorkIds = rows.Where(r => r.HasLeafWork).Select(r => new JobNodeId(r.Id)).ToList();

		// Same rule the single-node archive enforces (ADR 0044), applied across the whole subtree:
		// an archived leaf must not be left carrying a running session.
		if (leafWorkIds.Count > 0 && await context.Set<WorkSessionEntity>().AsNoTracking()
												  .AnyAsync(s => leafWorkIds.Contains(s.LeafWorkId) && s.FinishedAt == null, cancellationToken)
												  .ConfigureAwait(false)) {
			throw new InvariantViolationException(
				"leaf-closure-active-sessions", "This subtree cannot be archived while a session is active within it.");
		}

		var toArchive = await context.Set<JobNodeEntity>()
									 .Where(n => subtreeIds.Contains(n.Id) && n.ArchivedAt == null)
									 .ToListAsync(cancellationToken).ConfigureAwait(false);

		foreach (var affected in toArchive) {
			affected.ArchivedAt = now;
			affected.RowVersion += 1;
		}

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "archive-subtree", "job_node", root.Id.Value,
			request.Context.CorrelationId, null,
			new Dictionary<string, string?> {
				["node_count"] = rows.Count.ToString(CultureInfo.InvariantCulture),
				["already_archived_count"] = (rows.Count - toArchive.Count).ToString(CultureInfo.InvariantCulture),
			},
			new Dictionary<string, string?> {
				["archived_at"] = now.ToString(),
				["newly_archived_count"] = toArchive.Count.ToString(CultureInfo.InvariantCulture),
				["newly_archived_ids"] = string.Join(",", toArchive.Select(n => n.Id.Value)),
			});

		await JobNodeWriteExceptionTranslation.SaveChangesAndCommitAsync(context, transaction, cancellationToken).ConfigureAwait(false);

		return new() {
			NodeCount = rows.Count,
			NewlyArchivedCount = toArchive.Count,
		};
	}

	/// <inheritdoc />
	public async Task<LeafWorkResult> AttachLeafWorkAsync(AttachLeafWorkRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
												   .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		var node = await context.Set<JobNodeEntity>().AsNoTracking()
								.FirstOrDefaultAsync(n => n.Id == request.JobNodeId, cancellationToken).ConfigureAwait(false)
				   ?? throw new EntityNotFoundException($"Job node {request.JobNodeId} does not exist.");
		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.JobNodeId, now, cancellationToken).ConfigureAwait(false);

		if (await context.Set<LeafWorkEntity>().AsNoTracking()
						 .AnyAsync(lw => lw.JobNodeId == request.JobNodeId, cancellationToken).ConfigureAwait(false)) {
			throw new InvariantViolationException("leaf-work-already-attached", "This node already has LeafWork attached.");
		}

		var leafWork = await LeafWorkAttachSupport.CreateAsync(
			context, node, now, request.Context, request.PartialCriteria, request.FullCriteria,
			cancellationToken).ConfigureAwait(false);

		await JobNodeWriteExceptionTranslation.SaveChangesAndCommitForLeafWorkAttachAsync(context, transaction, cancellationToken)
											  .ConfigureAwait(false);

		return ToLeafWorkResult(leafWork);
	}

	/// <inheritdoc />
	public async Task<DecomposeWorkedLeafResult> DecomposeWorkedLeafAsync(
		DecomposeWorkedLeafRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
												   .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		var branch = await LoadTrackedNodeAsync(context, request.LeafNodeId, cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.LeafNodeId, now, cancellationToken).ConfigureAwait(false);
		CheckVersionOrThrow(branch.RowVersion, request.Version);

		if (branch.ParentId is null) {
			throw new InvariantViolationException("job-node-is-root-cannot-decompose", "The root job node cannot be decomposed.");
		}

		if (await context.Set<JobNodeEntity>().AsNoTracking()
						 .AnyAsync(c => c.ParentId == branch.Id, cancellationToken).ConfigureAwait(false)) {
			throw new InvariantViolationException(
				"job-node-has-children-cannot-decompose", "A node with children cannot be decomposed.");
		}

		var oldLeafWork = await context.Set<LeafWorkEntity>()
									   .FirstOrDefaultAsync(lw => lw.JobNodeId == request.LeafNodeId, cancellationToken).ConfigureAwait(false);
		if (oldLeafWork is null) {
			if (request.NewChildren.Count == 0) {
				throw new InvariantViolationException(
					"job-node-decompose-requires-a-child",
					"A leaf with no recorded work needs at least one new child to decompose into.");
			}
		} else if (string.IsNullOrWhiteSpace(request.ExistingWorkDescription)) {
			throw new ArgumentException(
				"ExistingWorkDescription is required when the node currently has LeafWork attached.", nameof(request));
		}

		foreach (var ownerUserId in request.NewChildren
										   .Select(child => child.OwnerUserId)
										   .Where(ownerUserId => ownerUserId.HasValue)
										   .Distinct()
										   .OrderBy(ownerUserId => ownerUserId!.Value.Value)) {
			await WorkflowEmployeeEligibility.EnsureMayBeAssignedWorkAsync(
				context, ownerUserId, now, "job-node-owner-not-eligible", cancellationToken).ConfigureAwait(false);
		}

		var (existingWorkChild, newChildren) = await JobNodeWriteExceptionTranslation.RunAndCommitAsync(
			transaction, ct => DecomposeAsync(context, branch, oldLeafWork, request, now, ct), cancellationToken).ConfigureAwait(false);

		return new() {
			BranchId = branch.Id,
			BranchVersion = branch.RowVersion,
			ExistingWorkChildId = existingWorkChild?.Id,
			NewChildIds = [.. newChildren.Select(c => c.Id)],
		};
	}


	/// <summary>
	///     The ordering below is load-bearing, not incidental (impl plan §7.3 step 4: "the highest-risk
	///     structural operation"). SQLite's leaf/branch-exclusivity triggers (schema 0006) are
	///     immediate, unlike PostgreSQL's deferred ones (<c>PostgreSqlJobNodeCommandPort</c> shares this
	///     exact ordering) -- every intermediate state below is therefore made individually valid, not
	///     just the final one:
	///     1. the child inheriting the existing LeafWork is created under <paramref name="branch" />'s own
	///     current parent, not under <paramref name="branch" /> itself, because <paramref name="branch" />
	///     still holds the old LeafWork at this point and an immediate trigger would abort otherwise;
	///     2. the LeafWork is moved onto that child via a new row plus delete, not an in-place update of
	///     its primary key, because <c>work_session.leaf_work_id</c>'s foreign key would reject
	///     repointing the key while sessions still reference the old value;
	///     3. sessions are repointed once the new LeafWork row exists;
	///     4. the old LeafWork row is removed once no session references it;
	///     5. only now -- with <paramref name="branch" /> holding no LeafWork -- are the newly identified
	///     children created, and the existing-work child reparented onto <paramref name="branch" />,
	///     which is finally converted from a leaf into their branch parent.
	/// </summary>
	private static async Task<(JobNodeEntity? ExistingWorkChild, List<JobNodeEntity> NewChildren)> DecomposeAsync(
		SqliteJobTrackDbContext context, JobNodeEntity branch, LeafWorkEntity? oldLeafWork,
		DecomposeWorkedLeafRequest request, Instant now, CancellationToken cancellationToken)
	{
		JobNodeEntity? existingWorkChild = null;
		if (oldLeafWork is not null) {
			existingWorkChild = new() {
				Id = default,
				ParentId = branch.ParentId,
				Description = request.ExistingWorkDescription!,
				PostedByUserId = request.Context.Actor,
				OwnerUserId = branch.OwnerUserId,
				Priority = branch.Priority,
				PostedAt = now,
				RowVersion = 1,
			};
			_ = context.Add(existingWorkChild);
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			var newLeafWork = new LeafWorkEntity {
				JobNodeId = existingWorkChild.Id,
				Achievement = oldLeafWork.Achievement,
				PartialCriteria = oldLeafWork.PartialCriteria,
				FullCriteria = oldLeafWork.FullCriteria,
				ChangedAt = oldLeafWork.ChangedAt,
				RowVersion = oldLeafWork.RowVersion + 1,
			};
			_ = context.Add(newLeafWork);
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			// A bulk operation, not a tracked-entity update, so it does not require loading a
			// potentially large number of sessions into memory; preserves every other column
			// (identifiers, users, times -- spec §4.5) untouched.
			_ = await context.Set<WorkSessionEntity>()
							 .Where(ws => ws.LeafWorkId == oldLeafWork.JobNodeId)
							 .ExecuteUpdateAsync(setters => setters.SetProperty(ws => ws.LeafWorkId, existingWorkChild.Id), cancellationToken)
							 .ConfigureAwait(false);

			_ = context.Remove(oldLeafWork);
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}

		var newChildren = new List<JobNodeEntity>();
		foreach (var child in request.NewChildren) {
			var newChild = new JobNodeEntity {
				Id = default,
				ParentId = branch.Id,
				Description = child.Description,
				WriteUp = child.WriteUp,
				PostedByUserId = request.Context.Actor,
				OwnerUserId = child.OwnerUserId,
				ExpectedDurationHours = child.ExpectedDurationHours,
				ExpectedCost = child.ExpectedCost,
				NeededStart = child.NeededStart,
				NeededFinish = child.NeededFinish,
				Priority = child.Priority,
				PostedAt = now,
				RowVersion = 1,
			};
			_ = context.Add(newChild);
			newChildren.Add(newChild);
		}

		var oldBranchDescription = branch.Description;
		if (existingWorkChild is not null) {
			existingWorkChild.ParentId = branch.Id;
			existingWorkChild.RowVersion += 1;
		}

		branch.Description = request.BranchDescription;
		branch.RowVersion += 1;
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "decompose-worked-leaf", "job_node", branch.Id.Value, request.Context.CorrelationId,
			null,
			new Dictionary<string, string?> {
				["description"] = oldBranchDescription,
				["kind"] = "Leaf",
			},
			new Dictionary<string, string?> {
				["description"] = branch.Description,
				["kind"] = "Branch",
				["existing_work_child_id"] = existingWorkChild?.Id.Value.ToString(CultureInfo.InvariantCulture),
				["new_child_ids"] = string.Join(',', newChildren.Select(c => c.Id.Value)),
			});
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		return (existingWorkChild, newChildren);
	}

	private static LeafWorkResult ToLeafWorkResult(LeafWorkEntity leafWork) => new() {
		JobNodeId = leafWork.JobNodeId,
		Achievement = leafWork.Achievement,
		PartialCriteria = leafWork.PartialCriteria,
		FullCriteria = leafWork.FullCriteria,
		ChangedAt = leafWork.ChangedAt,
		Version = leafWork.RowVersion,
	};

	private async Task<JobNodeResult> CreateAsync(CreateJobNodeRequest request, CancellationToken cancellationToken)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
												   .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.ParentId, now, cancellationToken).ConfigureAwait(false);
		await WorkflowEmployeeEligibility.EnsureMayBeAssignedWorkAsync(
			context, request.OwnerUserId, now, "job-node-owner-not-eligible", cancellationToken).ConfigureAwait(false);
		if (request.BeginWork is CreateJobNodeWorkSpec eligibilityCheck) {
			await WorkflowEmployeeEligibility.EnsureMayBeAssignedWorkAsync(
												 context, eligibilityCheck.WorkedByUserId, now, "work-session-target-not-eligible", cancellationToken)
											 .ConfigureAwait(false);
		}

		var node = new JobNodeEntity {
			Id = default,
			ParentId = request.ParentId,
			Description = request.Description,
			WriteUp = request.WriteUp,
			PostedByUserId = request.Context.Actor,
			// ADR 0048's session-start auto-claim, at create time: a node created into the unassigned
			// pool while someone begins work on it belongs to that worker, not to nobody. No conditional
			// claim is needed here the way PickUpAsync needs one -- the row does not exist yet, so no
			// concurrent claimant can be racing it.
			OwnerUserId = request.OwnerUserId ?? request.BeginWork?.WorkedByUserId,
			ExpectedDurationHours = request.ExpectedDurationHours,
			ExpectedCost = request.ExpectedCost,
			NeededStart = request.NeededStart,
			NeededFinish = request.NeededFinish,
			Priority = request.Priority,
			PostedAt = now,
			RowVersion = 1,
		};
		_ = context.Add(node);

		return await JobNodeWriteExceptionTranslation.RunAndCommitAsync(
			transaction,
			async ct => {
				_ = await context.SaveChangesAsync(ct).ConfigureAwait(false);

				AuditEventWriter.Add(
					context, request.Context.Actor, node.PostedAt, "create-job-node", "job_node",
					node.Id.Value, request.Context.CorrelationId, null, null, SnapshotJobNode(node));

				if (request.BeginWork is CreateJobNodeWorkSpec beginWork) {
					await BeginWorkOnNewNodeAsync(context, node, beginWork, request.Context, now, ct).ConfigureAwait(false);
				}

				_ = await context.SaveChangesAsync(ct).ConfigureAwait(false);

				return JobNodeStructuralProjection.ToResult(node, false, request.BeginWork is not null);
			},
			cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	///     Begins <paramref name="beginWork" />'s session on the just-created <paramref name="node" />,
	///     inside the create's own transaction: attach <c>LeafWork</c>, apply ADR 0038's
	///     <see cref="Achievement.Waiting" /> -&gt; <see cref="Achievement.InProgress" /> auto-advance, and
	///     open the session at the create instant. The row state and audit trail this leaves are exactly
	///     what <c>StartWorkAsync</c> against the freshly created node would leave -- same three events,
	///     same auto-advance reason, same <c>leaf_work</c> version -- so "create it and start it" is one
	///     transaction rather than two, without becoming a second, subtly different start path.
	///     <para>
	///         The prerequisite recheck is not redundant with the new node having no edges of its own: a
	///         leaf inherits every prerequisite attached to its ancestors, so it can be blocked the instant
	///         it exists, and spec §6 requires that recheck inside the write transaction.
	///     </para>
	/// </summary>
	private static async Task BeginWorkOnNewNodeAsync(
		SqliteJobTrackDbContext context, JobNodeEntity node, CreateJobNodeWorkSpec beginWork,
		CommandContext commandContext, Instant now, CancellationToken cancellationToken)
	{
		if (!await LeafReadiness.IsReadyAsync(context, node.Id, cancellationToken).ConfigureAwait(false)) {
			throw new PrerequisiteBlockedException($"Job node {node.Id}'s prerequisites are not satisfied.");
		}

		var leafWork = await LeafWorkAttachSupport.CreateAsync(
			context, node, now, commandContext, null, null, cancellationToken).ConfigureAwait(false);
		await LeafAchievementTransition.ApplyAsync(
										   context, leafWork, Achievement.InProgress, commandContext.Actor, now, commandContext.CorrelationId,
										   WorkAuditReasons.AutoAdvancedOnSessionStart, cancellationToken)
									   .ConfigureAwait(false);

		var session = new WorkSessionEntity {
			Id = default,
			LeafWorkId = node.Id,
			WorkedByUserId = beginWork.WorkedByUserId,
			StartedAt = now,
			FinishedAt = null,
			ChangedAt = now,
			RowVersion = 1,
		};
		_ = context.Add(session);

		// The session's own audit event names its database-generated identifier, so it can only be
		// queued once this save has produced one.
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		AuditEventWriter.Add(
			context, commandContext.Actor, now, "start-work-session", "work_session", session.Id.Value,
			commandContext.CorrelationId, null, null,
			new Dictionary<string, string?> {
				["leaf_work_id"] = session.LeafWorkId.Value.ToString(CultureInfo.InvariantCulture),
				["worked_by_user_id"] = session.WorkedByUserId.Value.ToString(CultureInfo.InvariantCulture),
				["started_at"] = session.StartedAt.ToString(),
			});
	}

	private Task<SqliteJobTrackDbContext> CreateOpenContextAsync(CancellationToken cancellationToken) =>
		SqliteDbContextFactory.CreateOpenContextAsync(connectionString, cancellationToken);

	private static async Task<JobNodeEntity> LoadTrackedNodeAsync(
		SqliteJobTrackDbContext context, JobNodeId nodeId, CancellationToken cancellationToken) =>
		await context.Set<JobNodeEntity>().FirstOrDefaultAsync(n => n.Id == nodeId, cancellationToken).ConfigureAwait(false)
		?? throw new EntityNotFoundException($"Job node {nodeId} does not exist.");

	private static async Task AuthorizeOrThrowAsync(
		SqliteJobTrackDbContext context, AppUserId actorId, JobNodeId nodeId, Instant now, CancellationToken cancellationToken)
	{
		var actorRoles = await GetActorRolesAsync(context, actorId, now, cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, actorRoles, actorId, nodeId, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	///     Overload for callers (e.g. <see cref="MoveAsync" />) that already loaded the actor's
	///     roles once and authorize against more than one node, so the identical role query does not
	///     run again per node.
	/// </summary>
	private static async Task AuthorizeOrThrowAsync(
		SqliteJobTrackDbContext context, EquatableArray<EmployeeRole> actorRoles, AppUserId actorId,
		JobNodeId nodeId, CancellationToken cancellationToken)
	{
		var ancestorOwnerIds = await JobNodeHierarchyQueries.GetAncestorOwnerIdsAsync(context, nodeId.Value, cancellationToken)
															.ConfigureAwait(false);

		if (ancestorOwnerIds.Count == 0) {
			throw new EntityNotFoundException($"Job node {nodeId} does not exist.");
		}

		if (!JobNodeAccessPolicy.CanManage(actorRoles, ancestorOwnerIds.Contains(actorId.Value))) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not manage job node {nodeId}.");
		}
	}

	private static async Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		SqliteJobTrackDbContext context, AppUserId actorId, Instant now, CancellationToken cancellationToken)
	{
		var actorIdentityUser = await context.Set<IdentityUserEntity>().AsNoTracking()
											 .FirstOrDefaultAsync(iu => iu.AppUserId == actorId, cancellationToken).ConfigureAwait(false)
								?? throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		ActorAccountState.EnsureMayAct(actorIdentityUser, actorId, now);

		var roles = await context.Set<IdentityUserRoleEntity>().AsNoTracking()
								 .Where(ur => ur.IdentityUserId == actorIdentityUser.Id)
								 .Select(ur => (EmployeeRole)ur.IdentityRoleId)
								 .ToArrayAsync(cancellationToken).ConfigureAwait(false);

		return [.. roles];
	}

	/// <summary>SQLite has no roles, so a plain set-based delete is correct here (contrast the PostgreSQL SECURITY DEFINER path).</summary>
	private static Task DeleteWorkSessionsForLeafWorkAsync(
		DbContext context, IReadOnlyList<JobNodeId> leafWorkIds, CancellationToken cancellationToken) =>
		context.Set<WorkSessionEntity>().Where(s => leafWorkIds.Contains(s.LeafWorkId)).ExecuteDeleteAsync(cancellationToken);

	private static void CheckVersionOrThrow(long currentVersion, long expectedVersion)
	{
		if (currentVersion != expectedVersion) {
			throw new ConcurrencyConflictException(
				$"Expected version {expectedVersion} but the current version is {currentVersion}.");
		}
	}

	/// <summary>
	///     Ownership model §2.1: the permanent root's owner may never be null. The database's root-owner
	///     CHECK is authoritative, but surfaces only as the generic "job-node-write-rejected" translation
	///     (impl plan §7.4) -- this application-side guard gives the specific, actionable error before
	///     ever reaching the database.
	/// </summary>
	private static void EnsureRootOwnerNotNulledOrThrow(JobNodeEntity node, AppUserId? requestedOwnerUserId)
	{
		if (node.ParentId is null && requestedOwnerUserId is null) {
			throw new InvariantViolationException(
				"job-node-root-owner-required", "The permanent root's owner cannot be null.");
		}
	}

	/// <summary>
	///     The audit before/after field snapshot for a <c>job_node</c> row (spec §16, ADR 0003
	///     "the full before and after row content"), used by every job-node mutation's audit event.
	/// </summary>
	private static Dictionary<string, string?> SnapshotJobNode(JobNodeEntity node) => new() {
		["parent_id"] = node.ParentId?.Value.ToString(CultureInfo.InvariantCulture),
		["description"] = node.Description,
		["write_up"] = node.WriteUp,
		["owner_user_id"] = node.OwnerUserId?.Value.ToString(CultureInfo.InvariantCulture),
		["priority"] = node.Priority.ToString(),
		["archived_at"] = node.ArchivedAt?.ToString(),
	};

	/// <summary>
	///     The audit before-snapshot for an administrator's force-delete of a worked leaf (ADR 0036):
	///     once committed, the <c>job_node</c>, <c>leaf_work</c>, and every <c>work_session</c> row are
	///     gone, so this is the only surviving record of what was destroyed. <c>audit_event.entity_id</c>
	///     is deliberately not a foreign key (schema version 0012), so this row is expected to outlive
	///     the entity it describes.
	/// </summary>
	private static Dictionary<string, string?> SnapshotWorkedLeaf(
		JobNodeEntity node, LeafWorkEntity leafWork, List<WorkSessionEntity> sessions)
	{
		var snapshot = SnapshotJobNode(node);
		snapshot["achievement"] = leafWork.Achievement.ToString();
		snapshot["partial_criteria"] = leafWork.PartialCriteria;
		snapshot["full_criteria"] = leafWork.FullCriteria;
		snapshot["work_session_count"] = sessions.Count.ToString(CultureInfo.InvariantCulture);
		snapshot["work_session_total_seconds"] = sessions
												 .Where(s => s.FinishedAt is not null)
												 .Sum(s => (s.FinishedAt!.Value - s.StartedAt).TotalSeconds)
												 .ToString(CultureInfo.InvariantCulture);
		return snapshot;
	}
}
