namespace JobTrack.Persistence.Sqlite;

using System.Data;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using Domain.Authorization;
using Domain.Hierarchy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared;
using Shared.Entities;

/// <summary>
///     SQLite implementation of <see cref="IJobRequestCommandPort" /> (ADR 0033). One
///     <see cref="SqliteJobTrackDbContext" />/connection/transaction per call; SQLite has no advisory
///     lock or stored function, so <see cref="IsolationLevel.Serializable" /> starts a
///     <c>BEGIN IMMEDIATE</c> transaction that serializes concurrent writes through SQLite's
///     single-writer model (matches <see cref="SqliteJobNodeCommandPort" />'s established use of the
///     same technique).
/// </summary>
internal sealed class SqliteJobRequestCommandPort : IJobRequestCommandPort
{
	private const int SqliteConstraintErrorCode = 19;
	private readonly IClock clock;
	private readonly string connectionString;

	/// <summary>Creates the port over the given SQLite connection string.</summary>
	public SqliteJobRequestCommandPort(string connectionString, IClock clock)
	{
		this.connectionString = connectionString;
		this.clock = clock;
	}

	/// <inheritdoc />
	public async Task<JobRequestResult> SubmitAsync(SubmitJobRequestRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		var actorRoles = await GetActorRolesAsync(context, request.Context.Actor, now, cancellationToken).ConfigureAwait(false);

		var holdingArea = await context.Set<RequestHoldingAreaEntity>().AsNoTracking()
							  .FirstOrDefaultAsync(h => h.Id == request.HoldingAreaId, cancellationToken).ConfigureAwait(false)
						  ?? throw new EntityNotFoundException($"Holding area {request.HoldingAreaId} does not exist.");

		var actorIsEligible = holdingArea.DepartmentId is null
							  || await context.Set<AppUserDepartmentEntity>().AsNoTracking()
								  .AnyAsync(
									  d => d.AppUserId == request.Context.Actor && d.DepartmentId == holdingArea.DepartmentId, cancellationToken)
								  .ConfigureAwait(false);

		if (!RequesterAccessPolicy.CanSubmit(actorRoles, holdingArea.IsActive, actorIsEligible)) {
			throw new AuthorizationDeniedException(
				$"Actor {request.Context.Actor} may not submit a request into holding area {request.HoldingAreaId}.");
		}

		var node = new JobNodeEntity {
			Id = default,
			ParentId = holdingArea.JobNodeId,
			Description = request.Description,
			WriteUp = request.WriteUp,
			PostedByUserId = request.Context.Actor,
			OwnerUserId = holdingArea.DefaultOwnerUserId,
			Priority = holdingArea.DefaultPriority,
			PostedAt = now,
			RowVersion = 1,
		};
		_ = context.Add(node);

		JobRequestEntity? jobRequest = null;
		await JobNodeWriteExceptionTranslation.SaveChangesAndCommitAsync(context, transaction, cancellationToken, ct => {
			jobRequest = new() {
				JobNodeId = node.Id,
				RequesterUserId = request.Context.Actor,
				HoldingAreaId = request.HoldingAreaId,
				RequesterReference = request.RequesterReference,
				SubmittedAt = now,
				RowVersion = 1,
			};
			_ = context.Add(jobRequest);

			AuditEventWriter.Add(
				context, request.Context.Actor, now, "submit-request", "job_request", node.Id.Value,
				request.Context.CorrelationId, null, null,
				new Dictionary<string, string?> {
					["holding_area_id"] = request.HoldingAreaId.Value.ToString(CultureInfo.InvariantCulture),
					["description"] = request.Description,
				});

			return Task.CompletedTask;
		}).ConfigureAwait(false);

		return ToResult(node, jobRequest!);
	}

	/// <inheritdoc />
	public async Task<JobNodeResult> MoveAsync(MoveRequesterJobRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		var actorRoles = await GetActorRolesAsync(context, request.Context.Actor, now, cancellationToken).ConfigureAwait(false);
		var ancestorOwnerIds = await RequireRequesterJobAsync(context, request.NodeId, cancellationToken).ConfigureAwait(false);

		if (!JobNodeAccessPolicy.CanManage(actorRoles, ancestorOwnerIds.Contains(request.Context.Actor.Value))) {
			throw new AuthorizationDeniedException($"Actor {request.Context.Actor} may not move requester job {request.NodeId}.");
		}

		var oldParentId = await context.Set<JobNodeEntity>().AsNoTracking()
			.Where(n => n.Id == request.NodeId).Select(n => n.ParentId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

		int affected;
		try {
			// job_node_no_cycle (schema version 0005) and the self-parent CHECK constraint
			// (schema version 0004) fire immediately from this UPDATE -- SQLite has no deferred
			// constraint triggers (impl plan §7.4). Only the authorization predicate differs from
			// the ordinary structural move (ADR 0033, plan §5).
			affected = await context.Database.ExecuteSqlInterpolatedAsync(
				$"""
				 UPDATE job_node SET parent_id = {request.NewParentId.Value}, row_version = row_version + 1
				 WHERE id = {request.NodeId.Value} AND row_version = {request.Version};
				 """, cancellationToken).ConfigureAwait(false);
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
			context, request.Context.Actor, now, "move-requester-job", "job_node",
			request.NodeId.Value, request.Context.CorrelationId, null,
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
	public async Task<EquatableArray<JobRequestSummaryResult>> GetMyRequestsAsync(
		CommandContext context, CancellationToken cancellationToken = default)
	{
		await using var dbContext = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);

		_ = await GetActorRolesAsync(dbContext, context.Actor, clock.GetCurrentInstant(), cancellationToken).ConfigureAwait(false);

		var rows = await (
			from jobRequest in dbContext.Set<JobRequestEntity>().AsNoTracking()
			join node in dbContext.Set<JobNodeEntity>().AsNoTracking() on jobRequest.JobNodeId equals node.Id
			where jobRequest.RequesterUserId == context.Actor
			orderby jobRequest.SubmittedAt descending
			select new JobRequestSummaryResult {
				JobNodeId = node.Id,
				Description = node.Description,
				SubmittedAt = jobRequest.SubmittedAt,
				Version = jobRequest.RowVersion,
			}).ToArrayAsync(cancellationToken).ConfigureAwait(false);

		return [.. rows];
	}

	/// <inheritdoc />
	public async Task<EquatableArray<HoldingAreaSummaryResult>> GetEligibleHoldingAreasAsync(
		CommandContext context, CancellationToken cancellationToken = default)
	{
		await using var dbContext = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);

		_ = await GetActorRolesAsync(dbContext, context.Actor, clock.GetCurrentInstant(), cancellationToken).ConfigureAwait(false);

		var rows = await dbContext.Set<RequestHoldingAreaEntity>().AsNoTracking()
			.Where(h => h.IsActive
						&& (h.DepartmentId == null
							|| dbContext.Set<AppUserDepartmentEntity>().Any(d => d.AppUserId == context.Actor && d.DepartmentId == h.DepartmentId)))
			.OrderBy(h => h.Name)
			.Select(h => new HoldingAreaSummaryResult { Id = h.Id, Name = h.Name })
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);

		return [.. rows];
	}

	/// <inheritdoc />
	public async Task<JobRequestResult> AcknowledgeAsync(AcknowledgeJobRequestRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		var actorRoles = await GetActorRolesAsync(context, request.Context.Actor, now, cancellationToken).ConfigureAwait(false);
		var ancestorOwnerIds = await RequireRequesterJobAsync(context, request.NodeId, cancellationToken).ConfigureAwait(false);

		if (!JobNodeAccessPolicy.CanManage(actorRoles, ancestorOwnerIds.Contains(request.Context.Actor.Value))) {
			throw new AuthorizationDeniedException($"Actor {request.Context.Actor} may not acknowledge requester job {request.NodeId}.");
		}

		var jobRequest = await context.Set<JobRequestEntity>().FirstAsync(r => r.JobNodeId == request.NodeId, cancellationToken)
			.ConfigureAwait(false);
		if (jobRequest.RowVersion != request.Version) {
			throw new ConcurrencyConflictException(
				$"Expected version {request.Version} for job request {request.NodeId} did not match its current version.");
		}

		if (jobRequest.AcknowledgedAt is not null) {
			throw new InvariantViolationException(
				"request-already-acknowledged",
				$"Job request {request.NodeId} has already been acknowledged.");
		}

		jobRequest.AcknowledgedAt = now;
		jobRequest.AcknowledgedByUserId = request.Context.Actor;
		jobRequest.RowVersion += 1;

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "acknowledge-request", "job_request", request.NodeId.Value,
			request.Context.CorrelationId, null, null,
			new Dictionary<string, string?> { ["acknowledged_by_user_id"] = request.Context.Actor.Value.ToString(CultureInfo.InvariantCulture) });

		await JobNodeWriteExceptionTranslation.SaveChangesAndCommitAsync(context, transaction, cancellationToken).ConfigureAwait(false);

		var node = await context.Set<JobNodeEntity>().AsNoTracking()
			.FirstAsync(n => n.Id == request.NodeId, cancellationToken).ConfigureAwait(false);

		return ToResult(node, jobRequest);
	}

	/// <inheritdoc />
	public async Task<JobRequestNoteResult> AddNoteAsync(AddJobRequestNoteRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		var actorRoles = await GetActorRolesAsync(context, request.Context.Actor, now, cancellationToken).ConfigureAwait(false);
		var ancestorOwnerIds = await RequireRequesterJobAsync(context, request.NodeId, cancellationToken).ConfigureAwait(false);

		var jobRequest = await context.Set<JobRequestEntity>().AsNoTracking()
			.FirstAsync(r => r.JobNodeId == request.NodeId, cancellationToken).ConfigureAwait(false);
		var controlsAnchor = ancestorOwnerIds.Contains(request.Context.Actor.Value);

		bool visibleToRequester;
		if (JobNodeAccessPolicy.CanManage(actorRoles, controlsAnchor)) {
			visibleToRequester = request.VisibleToRequester;
		} else if (jobRequest.RequesterUserId == request.Context.Actor
				   && RequesterAccessPolicy.CanCommentAsRequester(
					   actorRoles, true, false, false,
					   controlsAnchor, jobRequest.ClosedToRequesterAt is not null)) {
			visibleToRequester = true;
		} else {
			throw new AuthorizationDeniedException($"Actor {request.Context.Actor} may not add a note to request {request.NodeId}.");
		}

		var note = new JobRequestNoteEntity {
			Id = default,
			JobNodeId = request.NodeId,
			AuthorUserId = request.Context.Actor,
			Content = request.Content,
			IsVisibleToRequester = visibleToRequester,
			CreatedAt = now,
		};
		_ = context.Add(note);

		await JobNodeWriteExceptionTranslation.SaveChangesAndCommitAsync(context, transaction, cancellationToken, ct => {
			AuditEventWriter.Add(
				context, request.Context.Actor, now, "add-request-note", "job_request_note", note.Id.Value,
				request.Context.CorrelationId, null, null,
				new Dictionary<string, string?> { ["visible_to_requester"] = visibleToRequester.ToString(CultureInfo.InvariantCulture) });

			return Task.CompletedTask;
		}).ConfigureAwait(false);

		return ToResult(note);
	}

	/// <inheritdoc />
	public async Task<JobRequestDetailResult> GetDetailAsync(GetJobRequestDetailRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);

		var actorRoles = await GetActorRolesAsync(context, request.Context.Actor, clock.GetCurrentInstant(), cancellationToken)
			.ConfigureAwait(false);
		var ancestorOwnerIds = await RequireRequesterJobAsync(context, request.NodeId, cancellationToken).ConfigureAwait(false);

		var jobRequest = await context.Set<JobRequestEntity>().AsNoTracking()
			.FirstAsync(r => r.JobNodeId == request.NodeId, cancellationToken).ConfigureAwait(false);
		var node = await context.Set<JobNodeEntity>().AsNoTracking()
			.FirstAsync(n => n.Id == request.NodeId, cancellationToken).ConfigureAwait(false);
		var controlsAnchor = ancestorOwnerIds.Contains(request.Context.Actor.Value);

		if (!RequesterAccessPolicy.CanView(
				actorRoles, jobRequest.RequesterUserId == request.Context.Actor,
				false, false, controlsAnchor)) {
			throw new AuthorizationDeniedException($"Actor {request.Context.Actor} may not view request {request.NodeId}.");
		}

		var isStaffViewer = actorRoles.Contains(EmployeeRole.Administrator) || actorRoles.Contains(EmployeeRole.JobManager)
																			|| actorRoles.Contains(EmployeeRole.Worker);

		var subtreeRows = await JobNodeHierarchyQueries.GetRequesterSubtreeAsync(context, request.NodeId.Value, cancellationToken)
			.ConfigureAwait(false);
		var subtreeIds = subtreeRows.Select(r => new JobNodeId(r.Id)).ToArray();
		var postedAtByNodeId = await context.Set<JobNodeEntity>().AsNoTracking()
			.Where(n => subtreeIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, n => n.PostedAt, cancellationToken).ConfigureAwait(false);
		var changedAtByNodeId = await context.Set<LeafWorkEntity>().AsNoTracking()
			.Where(lw => subtreeIds.Contains(lw.JobNodeId))
			.ToDictionaryAsync(lw => lw.JobNodeId, lw => lw.ChangedAt, cancellationToken).ConfigureAwait(false);

		var acknowledged = jobRequest.AcknowledgedAt is not null;
		var overallStatus = RequesterStatusCalculator.Derive(acknowledged, ToLeafStates(subtreeRows.Where(r => r.IsChildless)));

		var subtree = subtreeRows.Select(r => new RequesterSubtreeNodeResult {
			JobNodeId = new(r.Id),
			Description = r.Description,
			Status = r.IsChildless
				? RequesterStatusCalculator.Derive(acknowledged, ToLeafStates([r]))
				: overallStatus,
			ParentId = r.Id == request.NodeId.Value ? null : new JobNodeId(r.ParentId!.Value),
			LastUpdatedAt = changedAtByNodeId.TryGetValue(new(r.Id), out var changedAt) ? changedAt : postedAtByNodeId[new(r.Id)],
		}).ToArray();

		var notes = await context.Set<JobRequestNoteEntity>().AsNoTracking()
			.Where(n => n.JobNodeId == request.NodeId && (isStaffViewer || n.IsVisibleToRequester))
			.OrderBy(n => n.CreatedAt)
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);

		return new() {
			JobNodeId = node.Id,
			Description = node.Description,
			Status = overallStatus,
			SubmittedAt = jobRequest.SubmittedAt,
			AcknowledgedAt = jobRequest.AcknowledgedAt,
			Version = jobRequest.RowVersion,
			Subtree = [.. subtree],
			Notes = [.. notes.Select(ToResult)],
		};
	}

	private static IReadOnlyCollection<RequesterSubtreeLeafState> ToLeafStates(IEnumerable<RequesterSubtreeRow> rows) => [
		.. rows.Select(r => new RequesterSubtreeLeafState { LeafAchievement = r.AchievementId.HasValue ? (Achievement)r.AchievementId.Value : null }),
	];

	/// <summary>
	///     Verifies <paramref name="nodeId" /> has an associated <c>job_request</c> row and returns its
	///     ancestor-owner set (also confirming the node exists), the shared precondition every
	///     requester-job-scoped write/read in this port needs (mirrors <see cref="MoveAsync" />'s own
	///     two-step check).
	/// </summary>
	private static async Task<IReadOnlyList<long>> RequireRequesterJobAsync(
		SqliteJobTrackDbContext context, JobNodeId nodeId, CancellationToken cancellationToken)
	{
		var nodeExists = await context.Set<JobNodeEntity>().AsNoTracking()
			.AnyAsync(n => n.Id == nodeId, cancellationToken).ConfigureAwait(false);
		if (!nodeExists) {
			throw new EntityNotFoundException($"Job node {nodeId} does not exist.");
		}

		var isRequesterJob = await context.Set<JobRequestEntity>().AsNoTracking()
			.AnyAsync(r => r.JobNodeId == nodeId, cancellationToken).ConfigureAwait(false);
		if (!isRequesterJob) {
			throw new InvariantViolationException("requester-job-required", $"Job node {nodeId} has no associated job_request row.");
		}

		var ancestorOwnerIds = await JobNodeHierarchyQueries.GetAncestorOwnerIdsAsync(context, nodeId.Value, cancellationToken)
			.ConfigureAwait(false);
		return ancestorOwnerIds;
	}

	private static JobRequestNoteResult ToResult(JobRequestNoteEntity note) => new() {
		Id = note.Id,
		AuthorUserId = note.AuthorUserId,
		Content = note.Content,
		VisibleToRequester = note.IsVisibleToRequester,
		CreatedAt = note.CreatedAt,
	};

	private Task<SqliteJobTrackDbContext> CreateOpenContextAsync(CancellationToken cancellationToken) =>
		SqliteDbContextFactory.CreateOpenContextAsync(connectionString, cancellationToken);

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

	private static JobRequestResult ToResult(JobNodeEntity node, JobRequestEntity jobRequest) => new() {
		JobNodeId = node.Id,
		HoldingAreaId = jobRequest.HoldingAreaId,
		RequesterUserId = jobRequest.RequesterUserId,
		OwnerUserId = node.OwnerUserId,
		Description = node.Description,
		SubmittedAt = jobRequest.SubmittedAt,
		AcknowledgedAt = jobRequest.AcknowledgedAt,
		Version = jobRequest.RowVersion,
	};
}
