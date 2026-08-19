namespace JobTrack.Persistence.Sqlite;

using System.Data;
using System.Globalization;
using Abstractions;
using Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared;
using Shared.Entities;

internal sealed partial class SqliteJobNodeCommandPort
{
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

		_ = context.Add(new JobPrerequisiteEntity {
			FromId = request.RequiredJobId,
			ToId = request.DependentJobId,
		});

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
				_ = context.Add(new JobPrerequisiteEntity {
					FromId = edge.RequiredJobId,
					ToId = edge.DependentJobId,
				});

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
		var ownerAssignees = request.Nodes
									.Where(node => node.OwnerUserId.HasValue)
									.Select(node => (UserId: node.OwnerUserId!.Value, ConstraintId: "job-node-owner-not-eligible"));
		var sessionAssignees = request.Nodes
									  .Where(node => node.LeafWork is not null)
									  .SelectMany(node => ImportedSessions(node.LeafWork!))
									  .Select(session => (UserId: session.WorkedByUserId, ConstraintId: "work-session-target-not-eligible"));
		var assignees = ownerAssignees.Concat(sessionAssignees)
									  .GroupBy(assignee => assignee.UserId)
									  .Select(group => group.First())
									  .OrderBy(assignee => assignee.UserId.Value)
									  .ToList();
		await IdentityUserWriteLock.AcquireManyAsync(
			context, assignees.Select(assignee => assignee.UserId).Concat(request.HomeNodeUserIds), cancellationToken).ConfigureAwait(false);
		foreach (var assignee in assignees) {
			await WorkflowEmployeeEligibility.EnsureMayBeAssignedWorkAsync(
				context, assignee.UserId, now, assignee.ConstraintId, cancellationToken).ConfigureAwait(false);
		}

		var created = await JobNodeWriteExceptionTranslation.RunAndCommitAsync(
			transaction, ct => ImportSubtreeCoreAsync(context, request, now, ct), cancellationToken).ConfigureAwait(false);

		return new() {
			Nodes = [
				.. created.Select(c => new ImportedJobNode {
					LocalId = c.LocalId, JobNodeId = c.Entity.Id,
				}),
			],
		};
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
				_ = context.Add(new JobPrerequisiteEntity {
					FromId = requiredId,
					ToId = dependentId,
				});
			}
		}

		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		await ImportRecordedWorkAsync(context, request, createdByLocalId, now, cancellationToken).ConfigureAwait(false);

		if (request.HomeNodeLocalId is long homeNodeLocalId) {
			await ImportHomeNodeAssignment.ApplyAsync(
											  context, createdByLocalId[homeNodeLocalId].Id, request.HomeNodeUserIds, request.Context.Actor, now,
											  request.Context.CorrelationId, cancellationToken)
										  .ConfigureAwait(false);
		}

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
	///         the end state is therefore equivalent to replaying those transitions -- which is why it
	///         goes through <c>LeafAchievementTransition.ApplyImportedAsync</c> and so carries ADR 0058's
	///         requester auto-acknowledgement, exactly as the equivalent replayed commands would.
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

			await LeafAchievementTransition.ApplyImportedAsync(
											   context, leafWork, work.Achievement, request.Context.Actor, now, request.Context.CorrelationId, cancellationToken)
										   .ConfigureAwait(false);

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
}
