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
internal sealed class SqliteJobNodeCommandPort : IJobNodeCommandPort
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
			new Dictionary<string, string?> { ["parent_id"] = oldParentId?.Value.ToString(CultureInfo.InvariantCulture) },
			new Dictionary<string, string?> { ["parent_id"] = request.NewParentId.Value.ToString(CultureInfo.InvariantCulture) });
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
			new Dictionary<string, string?> { ["archived_at"] = wasArchivedAt?.ToString() },
			new Dictionary<string, string?> { ["archived_at"] = node.ArchivedAt?.ToString() });

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

		AuditEventWriter.Add(
			context, request.Context.Actor, now, operation, "job_node", node.Id.Value,
			request.Context.CorrelationId, reason, before, null);

		_ = context.Remove(node);

		await JobNodeWriteExceptionTranslation.SaveChangesAndCommitForDeleteAsync(context, transaction, cancellationToken).ConfigureAwait(false);
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

		var oldLeafWork = await context.Set<LeafWorkEntity>()
							  .FirstOrDefaultAsync(lw => lw.JobNodeId == request.LeafNodeId, cancellationToken).ConfigureAwait(false)
						  ?? throw new InvariantViolationException("leaf-work-not-attached", "This node has no LeafWork to decompose.");

		var (existingWorkChild, newChildren) = await JobNodeWriteExceptionTranslation.RunAndCommitAsync(
			transaction, ct => DecomposeAsync(context, branch, oldLeafWork, request, now, ct), cancellationToken).ConfigureAwait(false);

		return new() {
			BranchId = branch.Id,
			BranchVersion = branch.RowVersion,
			ExistingWorkChildId = existingWorkChild.Id,
			NewChildIds = [.. newChildren.Select(c => c.Id)],
		};
	}

	/// <inheritdoc />
	public async Task AddPrerequisiteAsync(AddPrerequisiteRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		await ValidatePrerequisiteEdgeAsync(
				context, request.Context.Actor, request.RequiredJobId, request.DependentJobId, now, cancellationToken)
			.ConfigureAwait(false);

		_ = context.Add(new JobPrerequisiteEntity { FromId = request.RequiredJobId, ToId = request.DependentJobId });

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "add-job-prerequisite", "job_prerequisite",
			request.DependentJobId.Value, request.Context.CorrelationId, null, null,
			new Dictionary<string, string?> {
				["required_job_id"] = request.RequiredJobId.Value.ToString(CultureInfo.InvariantCulture),
				["dependent_job_id"] = request.DependentJobId.Value.ToString(CultureInfo.InvariantCulture),
			});

		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateException ex) when (FindSqliteException(ex) is SqliteException sqliteException) {
			throw new InvariantViolationException(
				"job-prerequisite-invalid", "This prerequisite edge violates a structural invariant.", sqliteException);
		}
	}

	/// <inheritdoc />
	public async Task AddPrerequisitesAsync(AddPrerequisitesRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
		var now = clock.GetCurrentInstant();

		try {
			foreach (var edge in request.Edges) {
				await ValidatePrerequisiteEdgeAsync(
						context, request.Context.Actor, edge.RequiredJobId, edge.DependentJobId, now, cancellationToken)
					.ConfigureAwait(false);
				_ = context.Add(new JobPrerequisiteEntity { FromId = edge.RequiredJobId, ToId = edge.DependentJobId });

				AuditEventWriter.Add(
					context, request.Context.Actor, now, "add-job-prerequisite", "job_prerequisite",
					edge.DependentJobId.Value, request.Context.CorrelationId, null, null,
					new Dictionary<string, string?> {
						["required_job_id"] = edge.RequiredJobId.Value.ToString(CultureInfo.InvariantCulture),
						["dependent_job_id"] = edge.DependentJobId.Value.ToString(CultureInfo.InvariantCulture),
					});
			}

			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateException ex) when (FindSqliteException(ex) is SqliteException sqliteException) {
			throw new InvariantViolationException(
				"job-prerequisite-invalid", "This prerequisite edge violates a structural invariant.", sqliteException);
		}
	}

	/// <inheritdoc />
	public async Task RemovePrerequisiteAsync(RemovePrerequisiteRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		var actorRoles = await GetActorRolesAsync(context, request.Context.Actor, now, cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, actorRoles, request.Context.Actor, request.RequiredJobId, cancellationToken)
			.ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, actorRoles, request.Context.Actor, request.DependentJobId, cancellationToken)
			.ConfigureAwait(false);

		var affected = await context.Set<JobPrerequisiteEntity>()
			.Where(p => p.FromId == request.RequiredJobId && p.ToId == request.DependentJobId)
			.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

		if (affected == 0) {
			throw new EntityNotFoundException(
				$"No prerequisite edge {request.RequiredJobId} -> {request.DependentJobId} exists.");
		}

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "remove-job-prerequisite", "job_prerequisite",
			request.DependentJobId.Value, request.Context.CorrelationId, null,
			new Dictionary<string, string?> {
				["required_job_id"] = request.RequiredJobId.Value.ToString(CultureInfo.InvariantCulture),
				["dependent_job_id"] = request.DependentJobId.Value.ToString(CultureInfo.InvariantCulture),
			},
			null);
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task<ImportSubtreeResult> ImportSubtreeAsync(ImportSubtreeRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.ParentId, now, cancellationToken).ConfigureAwait(false);

		var created = await JobNodeWriteExceptionTranslation.RunAndCommitAsync(
			transaction, ct => ImportSubtreeCoreAsync(context, request, now, ct), cancellationToken).ConfigureAwait(false);

		return new() { Nodes = [.. created.Select(c => new ImportedJobNode { LocalId = c.LocalId, JobNodeId = c.Entity.Id })] };
	}

	private static SqliteException? FindActiveSessionsViolation(Exception? ex) =>
		ex switch {
			null => null,
			SqliteException sqlite when sqlite.Message.Contains(ActiveSessionsMessage, StringComparison.Ordinal) => sqlite,
			_ => FindActiveSessionsViolation(ex.InnerException),
		};

	private static SqliteException? FindSqliteException(Exception? exception) =>
		exception switch {
			null => null,
			SqliteException sqliteException => sqliteException,
			_ => FindSqliteException(exception.InnerException),
		};

	/// <summary>
	///     Creates <paramref name="request" />'s already-ordered node batch (parents-before-children —
	///     <see cref="IJobCommands.ImportSubtreeAsync" /> guarantees this before calling the port) one at
	///     a time so each child's real, database-generated parent id is known before it is needed, then
	///     adds every prerequisite edge through the same <see cref="ValidatePrerequisiteEdgeAsync" /> the
	///     single-edge <see cref="AddPrerequisiteAsync" /> uses -- it sees this batch's own just-flushed
	///     rows via the same open connection/transaction, so ancestor and cycle checks work identically
	///     whether an edge's endpoints are pre-existing nodes or ones this same call just created.
	/// </summary>
	private static async Task<List<(long LocalId, JobNodeEntity Entity)>> ImportSubtreeCoreAsync(
		SqliteJobTrackDbContext context, ImportSubtreeRequest request, Instant now, CancellationToken cancellationToken)
	{
		var createdByLocalId = new Dictionary<long, JobNodeEntity>(request.Nodes.Count);
		var created = new List<(long LocalId, JobNodeEntity Entity)>(request.Nodes.Count);

		foreach (var spec in request.Nodes) {
			var parentId = spec.ParentLocalId.HasValue ? createdByLocalId[spec.ParentLocalId.Value].Id : request.ParentId;

			var node = new JobNodeEntity {
				Id = default,
				ParentId = parentId,
				Description = spec.Description,
				WriteUp = spec.WriteUp,
				PostedByUserId = request.Context.Actor,
				OwnerUserId = spec.OwnerUserId,
				ExpectedDurationHours = spec.ExpectedDurationHours,
				ExpectedCost = spec.ExpectedCost,
				NeededStart = spec.NeededStart,
				NeededFinish = spec.NeededFinish,
				Priority = spec.Priority,
				PostedAt = now,
				RowVersion = 1,
			};
			_ = context.Add(node);
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			createdByLocalId[spec.LocalId] = node;
			created.Add((spec.LocalId, node));
		}

		foreach (var spec in request.Nodes) {
			var dependentId = createdByLocalId[spec.LocalId].Id;
			foreach (var prerequisiteLocalId in spec.PrerequisiteLocalIds) {
				var requiredId = createdByLocalId[prerequisiteLocalId].Id;
				await ValidatePrerequisiteEdgeAsync(context, request.Context.Actor, requiredId, dependentId, now, cancellationToken)
					.ConfigureAwait(false);
				_ = context.Add(new JobPrerequisiteEntity { FromId = requiredId, ToId = dependentId });
			}
		}

		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		await ImportRecordedWorkAsync(context, request, createdByLocalId, now, cancellationToken).ConfigureAwait(false);

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "import-subtree", "job_node", request.ParentId.Value, request.Context.CorrelationId,
			null, null,
			new Dictionary<string, string?> {
				["node_count"] = created.Count.ToString(CultureInfo.InvariantCulture),
				["new_node_ids"] = string.Join(',', created.Select(c => c.Entity.Id.Value)),
			});
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		return created;
	}

	/// <summary>
	///     Records each <see cref="ImportSubtreeNodeSpec.LeafWork" /> in the batch -- attaching
	///     <c>LeafWork</c>, writing all supplied <c>work_session</c> rows, and setting its achievement -- inside
	///     the import's own transaction, so a tree imported with history behaves as if the equivalent
	///     start/finish/set-achievement commands had been replayed against it, without splitting the
	///     import across several transactions.
	///     <para>
	///         Work is applied in earliest supplied session-start order, which is
	///         exactly prerequisite order: <c>SubtreeImportPlanner</c> has already rejected any batch in
	///         which a leaf starts before a prerequisite of its finished, so replaying chronologically
	///         guarantees a dependent's gate is evaluated only once its prerequisites are already
	///         <see cref="Achievement.Success" /> in this transaction. The readiness recheck below is
	///         still the authority -- it sees prerequisites inherited from ancestors <em>outside</em> the
	///         batch, which the planner cannot know about.
	///     </para>
	///     <para>
	///         The final achievement is written directly rather than stepped through
	///         <see cref="Domain.Hierarchy.AchievementTransitions" />. That is not a bypass: because a
	///         session is always recorded, the leaf necessarily passes <c>Waiting -&gt; InProgress</c>,
	///         from which every achievement the planner admits (<c>InProgress</c>, <c>Success</c>,
	///         <c>Cancelled</c>, <c>Unsuccessful</c>) is a permitted next state under ADR 0001. Writing
	///         the end state is therefore equivalent to replaying those transitions.
	///     </para>
	/// </summary>
	private static async Task ImportRecordedWorkAsync(
		SqliteJobTrackDbContext context,
		ImportSubtreeRequest request,
		Dictionary<long, JobNodeEntity> createdByLocalId,
		Instant now,
		CancellationToken cancellationToken)
	{
		var workedSpecs = request.Nodes
			.Where(spec => spec.LeafWork is not null)
			.OrderBy(spec => ImportedSessions(spec.LeafWork!).Min(session => session.StartedAt))
			.ThenBy(spec => spec.LocalId)
			.ToList();

		foreach (var spec in workedSpecs) {
			var node = createdByLocalId[spec.LocalId];
			var work = spec.LeafWork!;

			var sessions = ImportedSessions(work).ToList();
			foreach (var session in sessions) {
				if (session.StartedAt > now) {
					throw new InvariantViolationException(
						"work-session-start-in-future", "A session's start instant must not be in the future.");
				}

				if (session.FinishedAt is Instant finishedAt && finishedAt > now) {
					throw new InvariantViolationException(
						"work-session-finish-in-future", "A session's finish instant must not be in the future.");
				}
			}

			var leafWork = await LeafWorkAttachSupport.CreateAsync(
				context, node, now, request.Context, null, null, cancellationToken).ConfigureAwait(false);

			foreach (var session in sessions) {
				_ = context.Add(new WorkSessionEntity {
					Id = default,
					LeafWorkId = node.Id,
					WorkedByUserId = session.WorkedByUserId,
					StartedAt = session.StartedAt,
					FinishedAt = session.FinishedAt,
					ChangedAt = now,
					RowVersion = 1,
				});
			}

			leafWork.Achievement = work.Achievement;
			leafWork.ChangedAt = now;

			// Flush before the recheck so this leaf's own rows, and every earlier leaf's achievement,
			// are visible to the readiness query.
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			if (!await LeafReadiness.IsReadyAsync(context, node.Id, cancellationToken).ConfigureAwait(false)) {
				throw new PrerequisiteBlockedException($"Job node {node.Id}'s prerequisites are not satisfied.");
			}

			AuditEventWriter.Add(
				context, request.Context.Actor, now, "import-leaf-work", "leaf_work", node.Id.Value,
				request.Context.CorrelationId, null, null,
				new Dictionary<string, string?> {
					["achievement"] = work.Achievement.ToString(),
					["session_count"] = sessions.Count.ToString(CultureInfo.InvariantCulture),
					["worked_by_user_id"] = work.WorkedByUserId.Value.ToString(CultureInfo.InvariantCulture),
					["started_at"] = work.StartedAt.ToString(),
					["finished_at"] = work.FinishedAt?.ToString(),
				});
		}

		if (workedSpecs.Count > 0) {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	private static IEnumerable<(AppUserId WorkedByUserId, Instant StartedAt, Instant? FinishedAt)> ImportedSessions(
		ImportSubtreeLeafWorkSpec work)
	{
		yield return (work.WorkedByUserId, work.StartedAt, work.FinishedAt);
		foreach (var session in work.AdditionalSessions) {
			yield return (session.WorkedByUserId, session.StartedAt, session.FinishedAt);
		}
	}

	/// <summary>
	///     Validates every <c>job_prerequisite</c> invariant application-side (spec §6 rules 2, 4, 5,
	///     plus the existing-edge check) before the write, so both providers report the same precise
	///     <see cref="InvariantViolationException.ConstraintId" /> for the common case -- SQLite's
	///     immediate triggers cannot distinguish a cycle from a hierarchy-edge violation by error code
	///     alone (unlike PostgreSQL, whose schema version 0017 gives each its own SQLSTATE), so this
	///     check is shared in spirit (duplicated per provider, matching this codebase's established
	///     convention) with <c>PostgreSqlJobNodeCommandPort</c>.
	/// </summary>
	private static async Task ValidatePrerequisiteEdgeAsync(
		SqliteJobTrackDbContext context, AppUserId actorId, JobNodeId requiredJobId, JobNodeId dependentJobId, Instant now,
		CancellationToken cancellationToken)
	{
		var actorRoles = await GetActorRolesAsync(context, actorId, now, cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, actorRoles, actorId, requiredJobId, cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, actorRoles, actorId, dependentJobId, cancellationToken).ConfigureAwait(false);

		if (requiredJobId == dependentJobId) {
			throw new InvariantViolationException("job-prerequisite-not-self", "A job cannot require itself.");
		}

		var dependentAncestorIds = await JobNodeHierarchyQueries.GetAncestorIdsAsync(context, dependentJobId.Value, cancellationToken)
			.ConfigureAwait(false);
		var requiredAncestorIds = await JobNodeHierarchyQueries.GetAncestorIdsAsync(context, requiredJobId.Value, cancellationToken)
			.ConfigureAwait(false);
		if (dependentAncestorIds.Contains(requiredJobId.Value) || requiredAncestorIds.Contains(dependentJobId.Value)) {
			throw new InvariantViolationException(
				"job-prerequisite-is-hierarchy-edge",
				"A prerequisite edge cannot connect nodes that are ancestor/descendant of each other.");
		}

		if (await context.Set<JobPrerequisiteEntity>().AsNoTracking()
				.AnyAsync(jp => jp.FromId == requiredJobId && jp.ToId == dependentJobId, cancellationToken).ConfigureAwait(false)) {
			throw new InvariantViolationException("job-prerequisite-already-exists", "This prerequisite edge already exists.");
		}

		if (await JobNodeHierarchyQueries.PrerequisiteWouldCreateCycleAsync(
				context, requiredJobId.Value, dependentJobId.Value, cancellationToken).ConfigureAwait(false)) {
			throw new InvariantViolationException("job-prerequisite-would-cycle", "This prerequisite edge would create a cycle.");
		}
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
	private static async Task<(JobNodeEntity ExistingWorkChild, List<JobNodeEntity> NewChildren)> DecomposeAsync(
		SqliteJobTrackDbContext context, JobNodeEntity branch, LeafWorkEntity oldLeafWork,
		DecomposeWorkedLeafRequest request, Instant now, CancellationToken cancellationToken)
	{
		var existingWorkChild = new JobNodeEntity {
			Id = default,
			ParentId = branch.ParentId,
			Description = request.ExistingWorkDescription,
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
		existingWorkChild.ParentId = branch.Id;
		existingWorkChild.RowVersion += 1;
		branch.Description = request.BranchDescription;
		branch.RowVersion += 1;
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "decompose-worked-leaf", "job_node", branch.Id.Value, request.Context.CorrelationId,
			null,
			new Dictionary<string, string?> { ["description"] = oldBranchDescription, ["kind"] = "Leaf" },
			new Dictionary<string, string?> {
				["description"] = branch.Description,
				["kind"] = "Branch",
				["existing_work_child_id"] = existingWorkChild.Id.Value.ToString(CultureInfo.InvariantCulture),
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

		var node = new JobNodeEntity {
			Id = default,
			ParentId = request.ParentId,
			Description = request.Description,
			WriteUp = request.WriteUp,
			PostedByUserId = request.Context.Actor,
			OwnerUserId = request.OwnerUserId,
			ExpectedDurationHours = request.ExpectedDurationHours,
			ExpectedCost = request.ExpectedCost,
			NeededStart = request.NeededStart,
			NeededFinish = request.NeededFinish,
			Priority = request.Priority,
			PostedAt = now,
			RowVersion = 1,
		};
		_ = context.Add(node);

		await JobNodeWriteExceptionTranslation.SaveChangesAndCommitAsync(context, transaction, cancellationToken, ct => {
			AuditEventWriter.Add(
				context, request.Context.Actor, node.PostedAt, "create-job-node", "job_node",
				node.Id.Value, request.Context.CorrelationId, null, null, SnapshotJobNode(node));
			return Task.CompletedTask;
		}).ConfigureAwait(false);

		return JobNodeStructuralProjection.ToResult(node, false, false);
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
