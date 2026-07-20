namespace JobTrack.Persistence.PostgreSql;

using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using Domain.Authorization;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Shared;
using Shared.Entities;

/// <summary>
///     PostgreSQL implementation of <see cref="IWorkSessionCommandPort" /> (impl plan §7.3 slice 6:
///     start, finish, resume, and correct work sessions). One <see cref="PostgreSqlJobTrackDbContext" />
///     /connection/transaction per call, reloading the actor's current roles and whether the session
///     is (or will be) their own and applying <see cref="WorkSessionAccessPolicy" /> itself inside that
///     transaction, per the port's own contract. There is no advisory lock domain for work sessions
///     (ADR 0012 lists only schema-deployment, subtree-move/decomposition, bootstrap, and
///     prerequisite-graph-writes): same-user/same-leaf overlap is enforced purely by schema version
///     0007's GiST exclusion constraint and partial unique index, so a concurrent conflict is caught by
///     translating the resulting <see cref="PostgresException" />, not by taking a lock.
/// </summary>
internal sealed class PostgreSqlWorkSessionCommandPort : IWorkSessionCommandPort
{
	private readonly IClock clock;
	private readonly NpgsqlDataSource dataSource;

	/// <summary>Creates the port over the given pooled <see cref="NpgsqlDataSource" />.</summary>
	public PostgreSqlWorkSessionCommandPort(NpgsqlDataSource dataSource, IClock clock)
	{
		this.dataSource = dataSource;
		this.clock = clock;
	}

	/// <inheritdoc />
	public async Task<WorkSessionResult> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		if (!await context.Set<LeafWorkEntity>().AsNoTracking()
				.AnyAsync(lw => lw.JobNodeId == request.LeafWorkId, cancellationToken).ConfigureAwait(false)) {
			throw new EntityNotFoundException($"Job node {request.LeafWorkId} has no LeafWork attached.");
		}

		var now = clock.GetCurrentInstant();
		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.LeafWorkId, now, cancellationToken).ConfigureAwait(false);

		if (!await LeafReadiness.IsReadyAsync(context, request.LeafWorkId, cancellationToken).ConfigureAwait(false)) {
			throw new PrerequisiteBlockedException($"Job node {request.LeafWorkId}'s prerequisites are not satisfied.");
		}

		var startedAt = request.StartedAt ?? now;
		if (startedAt > now) {
			throw new InvariantViolationException(
				"work-session-start-in-future", "A session's start instant must not be in the future.");
		}

		if (await context.Set<WorkSessionEntity>().AsNoTracking().AnyAsync(
				s => s.LeafWorkId == request.LeafWorkId && s.WorkedByUserId == request.WorkedByUserId && s.FinishedAt == null,
				cancellationToken).ConfigureAwait(false)) {
			throw new InvariantViolationException(
				"work-session-already-active", "This worker already has an active session for this leaf.");
		}

		var session = new WorkSessionEntity {
			Id = default,
			LeafWorkId = request.LeafWorkId,
			WorkedByUserId = request.WorkedByUserId,
			StartedAt = startedAt,
			FinishedAt = null,
			ChangedAt = now,
			RowVersion = 1,
		};
		_ = context.Add(session);

		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			AuditEventWriter.Add(
				context, request.Context.Actor, now, "start-work-session", "work_session", session.Id.Value, request.Context.CorrelationId,
				null, null,
				new Dictionary<string, string?> {
					["leaf_work_id"] = session.LeafWorkId.Value.ToString(CultureInfo.InvariantCulture),
					["worked_by_user_id"] = session.WorkedByUserId.Value.ToString(CultureInfo.InvariantCulture),
					["started_at"] = session.StartedAt.ToString(),
				});
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (FindActiveSessionUniqueViolation(ex) is not null) {
			throw new InvariantViolationException(
				"work-session-already-active", "This worker already has an active session for this leaf.", ex);
		}
		catch (Exception ex) when (FindGeneralOverlapViolation(ex) is not null) {
			throw new InvariantViolationException(
				"work-session-overlap", "This session would overlap another session for the same worker and leaf.", ex);
		}

		return ToResult(session);
	}

	/// <inheritdoc />
	public async Task<WorkSessionResult> StartWorkAsync(StartWorkRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		var node = await context.Set<JobNodeEntity>().AsNoTracking()
					   .FirstOrDefaultAsync(n => n.Id == request.JobNodeId, cancellationToken).ConfigureAwait(false)
				   ?? throw new EntityNotFoundException($"Job node {request.JobNodeId} does not exist.");
		var now = clock.GetCurrentInstant();
		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.JobNodeId, now, cancellationToken).ConfigureAwait(false);

		var leafWork = await context.Set<LeafWorkEntity>()
			.FirstOrDefaultAsync(lw => lw.JobNodeId == request.JobNodeId, cancellationToken).ConfigureAwait(false);
		leafWork ??= await LeafWorkAttachSupport.CreateAsync(
			context, node, now, request.Context, null, null, cancellationToken).ConfigureAwait(false);

		if (!await LeafReadiness.IsReadyAsync(context, request.JobNodeId, cancellationToken).ConfigureAwait(false)) {
			throw new PrerequisiteBlockedException($"Job node {request.JobNodeId}'s prerequisites are not satisfied.");
		}

		var startedAt = request.StartedAt ?? now;
		if (startedAt > now) {
			throw new InvariantViolationException(
				"work-session-start-in-future", "A session's start instant must not be in the future.");
		}

		if (await context.Set<WorkSessionEntity>().AsNoTracking().AnyAsync(
				s => s.LeafWorkId == request.JobNodeId && s.WorkedByUserId == request.WorkedByUserId && s.FinishedAt == null,
				cancellationToken).ConfigureAwait(false)) {
			throw new InvariantViolationException(
				"work-session-already-active", "This worker already has an active session for this leaf.");
		}

		if (leafWork.Achievement == Achievement.Waiting) {
			var previousAchievement = leafWork.Achievement;
			leafWork.Achievement = Achievement.InProgress;
			leafWork.ChangedAt = now;
			leafWork.RowVersion += 1;

			AuditEventWriter.Add(
				context, request.Context.Actor, now, "set-achievement", "leaf_work", leafWork.JobNodeId.Value,
				request.Context.CorrelationId, "Advanced automatically on session start",
				new Dictionary<string, string?> { ["achievement"] = previousAchievement.ToString() },
				new Dictionary<string, string?> { ["achievement"] = leafWork.Achievement.ToString() });
		}

		var session = new WorkSessionEntity {
			Id = default,
			LeafWorkId = request.JobNodeId,
			WorkedByUserId = request.WorkedByUserId,
			StartedAt = startedAt,
			FinishedAt = null,
			ChangedAt = now,
			RowVersion = 1,
		};
		_ = context.Add(session);

		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			AuditEventWriter.Add(
				context, request.Context.Actor, now, "start-work-session", "work_session", session.Id.Value, request.Context.CorrelationId,
				null, null,
				new Dictionary<string, string?> {
					["leaf_work_id"] = session.LeafWorkId.Value.ToString(CultureInfo.InvariantCulture),
					["worked_by_user_id"] = session.WorkedByUserId.Value.ToString(CultureInfo.InvariantCulture),
					["started_at"] = session.StartedAt.ToString(),
				});
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (FindActiveSessionUniqueViolation(ex) is not null) {
			throw new InvariantViolationException(
				"work-session-already-active", "This worker already has an active session for this leaf.", ex);
		}
		catch (Exception ex) when (FindGeneralOverlapViolation(ex) is not null) {
			throw new InvariantViolationException(
				"work-session-overlap", "This session would overlap another session for the same worker and leaf.", ex);
		}

		return ToResult(session);
	}

	/// <inheritdoc />
	public async Task<WorkSessionResult> FinishSessionAsync(FinishSessionRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		var session = await LoadTrackedSessionAsync(context, request.SessionId, cancellationToken).ConfigureAwait(false);
		EnsureLeafMatchesOrThrow(session, request.LeafWorkId);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, session.LeafWorkId, now, cancellationToken).ConfigureAwait(false);
		CheckVersionOrThrow(session.RowVersion, request.Version);

		var finishedAt = request.FinishedAt ?? now;
		if (finishedAt <= session.StartedAt) {
			throw new InvariantViolationException(
				"work-session-invalid-interval", "A session's finish instant must be after its start instant.");
		}

		if (finishedAt > now) {
			throw new InvariantViolationException(
				"work-session-finish-in-future", "A session's finish instant must not be in the future.");
		}

		session.FinishedAt = finishedAt;
		session.ChangedAt = now;
		session.RowVersion += 1;

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "finish-work-session", "work_session", session.Id.Value, request.Context.CorrelationId,
			null,
			new Dictionary<string, string?> { ["finished_at"] = null },
			new Dictionary<string, string?> { ["finished_at"] = session.FinishedAt?.ToString() });

		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException ex) {
			throw new ConcurrencyConflictException(
				$"Expected version {request.Version} for work session {request.SessionId} did not match its current version.", ex);
		}

		return ToResult(session);
	}

	/// <inheritdoc />
	public async Task<WorkSessionResult> CorrectSessionAsync(CorrectSessionRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		var session = await LoadTrackedSessionAsync(context, request.SessionId, cancellationToken).ConfigureAwait(false);
		EnsureLeafMatchesOrThrow(session, request.LeafWorkId);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, session.LeafWorkId, now, cancellationToken).ConfigureAwait(false);
		CheckVersionOrThrow(session.RowVersion, request.Version);

		if (request.FinishedAt is Instant finishedAt && finishedAt <= request.StartedAt) {
			throw new InvariantViolationException(
				"work-session-invalid-interval", "A session's finish instant must be after its start instant.");
		}

		var before = new Dictionary<string, string?> {
			["started_at"] = session.StartedAt.ToString(),
			["finished_at"] = session.FinishedAt?.ToString(),
		};

		session.StartedAt = request.StartedAt;
		session.FinishedAt = request.FinishedAt;
		session.ChangedAt = now;
		session.RowVersion += 1;

		AuditEventWriter.Add(
			context, request.Context.Actor, session.ChangedAt, "correct-work-session", "work_session", session.Id.Value,
			request.Context.CorrelationId, request.Reason, before,
			new Dictionary<string, string?> { ["started_at"] = session.StartedAt.ToString(), ["finished_at"] = session.FinishedAt?.ToString() });

		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException ex) {
			throw new ConcurrencyConflictException(
				$"Expected version {request.Version} for work session {request.SessionId} did not match its current version.", ex);
		}
		catch (Exception ex) when (FindOverlapException(ex) is not null) {
			throw new InvariantViolationException(
				"work-session-overlap", "This correction would overlap another session for the same worker and leaf.", ex);
		}

		return ToResult(session);
	}

	/// <summary>
	///     The common "start/correct into a second active session" case is a plain unique-violation
	///     against schema version 0007's partial index; the rarer general-overlap case is the GiST
	///     exclusion constraint itself, whose concurrent-conflict path the spike behind that schema
	///     version found surfaces as a deadlock rather than a clean exclusion violation. EF's Npgsql
	///     execution strategy classifies both as potentially transient and re-wraps them (even on a
	///     single, non-retried attempt) in an outer <see cref="InvalidOperationException" /> rather than
	///     leaving <see cref="Npgsql.PostgresException" /> as the direct <see cref="Exception.InnerException" />
	///     of the <see cref="DbUpdateException" />, so this walks the whole chain rather than checking one level.
	/// </summary>
	private static PostgresException? FindOverlapException(Exception? ex) =>
		ex switch {
			null => null,
			PostgresException pg when IsOverlapSqlState(pg.SqlState) => pg,
			_ => FindOverlapException(ex.InnerException),
		};

	private static bool IsOverlapSqlState(string? sqlState) =>
		sqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ExclusionViolation or PostgresErrorCodes.DeadlockDetected;

	/// <summary>
	///     Unlike <see cref="FindOverlapException" /> (used where only one message is ever meaningful at
	///     the call site), a backdated <see cref="StartSessionRequest.StartedAt" /> can plausibly hit
	///     either schema version 0007 constraint, so <see cref="StartSessionAsync" /> disambiguates: the
	///     plain unique-violation (23505) against <c>work_session_one_active_per_leaf_user_idx</c> means
	///     "already active"; the GiST exclusion (23P01) or its belt-and-braces deadlock (40P01) path
	///     means the backdated interval collides with a different, already-recorded session.
	/// </summary>
	private static PostgresException? FindActiveSessionUniqueViolation(Exception? ex) =>
		ex switch {
			null => null,
			PostgresException pg when pg.SqlState == PostgresErrorCodes.UniqueViolation => pg,
			_ => FindActiveSessionUniqueViolation(ex.InnerException),
		};

	/// <inheritdoc cref="FindActiveSessionUniqueViolation" />
	private static PostgresException? FindGeneralOverlapViolation(Exception? ex) =>
		ex switch {
			null => null,
			PostgresException pg when pg.SqlState is PostgresErrorCodes.ExclusionViolation or PostgresErrorCodes.DeadlockDetected => pg,
			_ => FindGeneralOverlapViolation(ex.InnerException),
		};

	private PostgreSqlJobTrackDbContext CreateContext()
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, o => o.UseNodaTime())
			.Options;

		return new(options);
	}

	private static async Task<WorkSessionEntity> LoadTrackedSessionAsync(
		PostgreSqlJobTrackDbContext context, WorkSessionId sessionId, CancellationToken cancellationToken) =>
		await context.Set<WorkSessionEntity>().FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken).ConfigureAwait(false)
		?? throw new EntityNotFoundException($"Work session {sessionId} does not exist.");

	/// <summary>
	///     A nested route's parent identifier (e.g. <c>/jobs/{nodeId}/sessions/{sessionId}/finish</c>)
	///     must actually match the session, or the mismatch is treated identically to a nonexistent
	///     session (remediation plan §3.5) -- checked before authorization, alongside the existence
	///     check <see cref="LoadTrackedSessionAsync" /> already performs.
	/// </summary>
	private static void EnsureLeafMatchesOrThrow(WorkSessionEntity session, JobNodeId? expectedLeafWorkId)
	{
		if (expectedLeafWorkId is JobNodeId leafWorkId && session.LeafWorkId != leafWorkId) {
			throw new EntityNotFoundException($"Work session {session.Id} does not exist under job node {leafWorkId}.");
		}
	}

	/// <summary>
	///     Owner-gated recording (ownership model §4.2; ADR 0032): the actor may manage a session on
	///     <paramref name="leafId" /> if they control it -- directly own it or an ancestor, reusing the
	///     same ancestor-owner walk <c>PostgreSqlJobNodeCommandPort</c> performs for structural commands
	///     (impl plan's risk note: the two ports must compute the same control set, not duplicate the
	///     walk divergently) -- or hold Administrator/JobManager.
	/// </summary>
	private static async Task AuthorizeOrThrowAsync(
		PostgreSqlJobTrackDbContext context, AppUserId actorId, JobNodeId leafId, Instant now, CancellationToken cancellationToken)
	{
		var actorRoles = await GetActorRolesAsync(context, actorId, now, cancellationToken).ConfigureAwait(false);
		var ancestorOwnerIds = await JobNodeHierarchyQueries.GetAncestorOwnerIdsAsync(context, leafId.Value, cancellationToken)
			.ConfigureAwait(false);

		if (!WorkSessionAccessPolicy.CanManage(actorRoles, ancestorOwnerIds.Contains(actorId.Value))) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not manage a session on job node {leafId}.");
		}
	}

	private static async Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		PostgreSqlJobTrackDbContext context, AppUserId actorId, Instant now, CancellationToken cancellationToken)
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

	private static WorkSessionResult ToResult(WorkSessionEntity session) => new() {
		Id = session.Id,
		LeafWorkId = session.LeafWorkId,
		WorkedByUserId = session.WorkedByUserId,
		StartedAt = session.StartedAt,
		FinishedAt = session.FinishedAt,
		ChangedAt = session.ChangedAt,
		Version = session.RowVersion,
	};
}
