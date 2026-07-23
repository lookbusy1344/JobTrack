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
///     SQLite implementation of <see cref="IWorkSessionCommandPort" /> (impl plan §7.3 slice 6: start,
///     finish, resume, and correct work sessions). One <see cref="SqliteJobTrackDbContext" />/connection/
///     transaction per call; SQLite has no advisory lock or stored function, so
///     <see cref="IsolationLevel.Serializable" /> starts a <c>BEGIN IMMEDIATE</c> transaction that
///     serializes concurrent writes through SQLite's single-writer model (matches
///     <see cref="SqliteJobNodeCommandPort" />'s established use of the same technique). Same-user/
///     same-leaf overlap is enforced by schema version 0007's immediate triggers plus a partial unique
///     index, not by a lock; ADR 0044's closed-leaf check is enforced the same way (immediate triggers,
///     serialized by the same <c>BEGIN IMMEDIATE</c> transaction, no separate lock).
/// </summary>
internal sealed class SqliteWorkSessionCommandPort : IWorkSessionCommandPort
{
	/// <summary>
	///     SQLite's <c>SQLITE_CONSTRAINT</c> primary result code (sqlite3.h): the base code
	///     shared by <c>work_session_no_same_leaf_user_overlap_on_insert/on_update</c>'s
	///     <c>RAISE(ABORT, ...)</c> and the <c>work_session_one_active_per_leaf_user_idx</c> partial
	///     unique index (schema version 0007), matching <see cref="SqliteJobNodeCommandPort" />'s own use
	///     of this code for its own immediate-trigger violations.
	/// </summary>
	private const int SqliteConstraintErrorCode = 19;

	/// <summary>
	///     SQLite's extended result code for a unique-index violation
	///     (<c>SQLITE_CONSTRAINT_UNIQUE</c>, sqlite3.h): identifies
	///     <c>work_session_one_active_per_leaf_user_idx</c> specifically, distinct from the trigger-based
	///     general-overlap violation below (both share the base <see cref="SqliteConstraintErrorCode" />).
	/// </summary>
	private const int SqliteUniqueConstraintErrorCode = 2067;

	/// <summary>
	///     ADR 0044: the literal message <c>work_session_leaf_not_closed_on_insert/on_update</c>
	///     (schema version 0007) raise via <c>RAISE(ABORT, ...)</c> -- SQLite gives triggers no distinct
	///     extended error code the way the unique index above gets one, so this backstop disambiguates
	///     by message content instead.
	/// </summary>
	private const string LeafClosedMessage = "work-session-leaf-closed";

	/// <summary>ADR 0044: the literal message the leaf-closure-active-sessions immediate trigger raises.</summary>
	private const string ActiveSessionsMessage = "leaf-closure-active-sessions";

	/// <summary>ADR 0045 §4: the fixed structured reason recorded for every <see cref="CompleteLeafAsync" /> completion.</summary>
	private const string CompletionReason = "Completed from the leaf work page";

	/// <summary>ADR 0038's existing fixed auto-advance audit reason, reused verbatim by <see cref="ReopenAndStartWorkAsync" />.</summary>
	private const string AutoAdvanceReason = "Advanced automatically on session start";

	/// <summary>ADR 0048's fixed audit reason for a session-start-triggered pickup, distinguishing it from an explicit one.</summary>
	private const string AutoClaimReason = "Automatically claimed on session start";

	private readonly IClock clock;

	private readonly string connectionString;

	/// <summary>Creates the port over the given SQLite connection string.</summary>
	public SqliteWorkSessionCommandPort(string connectionString, IClock clock)
	{
		this.connectionString = connectionString;
		this.clock = clock;
	}

	/// <inheritdoc />
	public async Task<WorkSessionResult> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		if (!await context.Set<LeafWorkEntity>().AsNoTracking()
				.AnyAsync(lw => lw.JobNodeId == request.LeafWorkId, cancellationToken).ConfigureAwait(false)) {
			throw new EntityNotFoundException($"Job node {request.LeafWorkId} has no LeafWork attached.");
		}

		var now = clock.GetCurrentInstant();
		await AutoClaimUnassignedNodeAsync(context, request.Context, request.LeafWorkId, request.WorkedByUserId, now, cancellationToken)
			.ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.LeafWorkId, now, cancellationToken).ConfigureAwait(false);
		await EnsureTargetWorkerEligibleAsync(context, request.Context.Actor, request.WorkedByUserId, now, cancellationToken)
			.ConfigureAwait(false);

		if (await LeafSessionClosure.IsClosedAsync(context, request.LeafWorkId, cancellationToken).ConfigureAwait(false)) {
			throw new InvariantViolationException(
				"work-session-leaf-closed", "This leaf is closed to new sessions (terminal achievement or archived).");
		}

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
		catch (Exception ex) when (FindLeafClosedViolation(ex) is not null) {
			throw new InvariantViolationException(
				"work-session-leaf-closed", "This leaf is closed to new sessions (terminal achievement or archived).", ex);
		}
		catch (Exception ex) when (FindOverlapException(ex) is not null) {
			throw new InvariantViolationException(
				"work-session-overlap", "This session would overlap another session for the same worker and leaf.", ex);
		}

		return ToResult(session);
	}

	/// <inheritdoc />
	public async Task<WorkSessionResult> StartWorkAsync(StartWorkRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var node = await context.Set<JobNodeEntity>().AsNoTracking()
					   .FirstOrDefaultAsync(n => n.Id == request.JobNodeId, cancellationToken).ConfigureAwait(false)
				   ?? throw new EntityNotFoundException($"Job node {request.JobNodeId} does not exist.");
		var now = clock.GetCurrentInstant();
		await AutoClaimUnassignedNodeAsync(context, request.Context, request.JobNodeId, request.WorkedByUserId, now, cancellationToken)
			.ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.JobNodeId, now, cancellationToken).ConfigureAwait(false);
		await EnsureTargetWorkerEligibleAsync(context, request.Context.Actor, request.WorkedByUserId, now, cancellationToken)
			.ConfigureAwait(false);

		var leafWork = await context.Set<LeafWorkEntity>()
			.FirstOrDefaultAsync(lw => lw.JobNodeId == request.JobNodeId, cancellationToken).ConfigureAwait(false);
		leafWork ??= await LeafWorkAttachSupport.CreateAsync(
			context, node, now, request.Context, null, null, cancellationToken).ConfigureAwait(false);

		if (AchievementTransitions.IsCompletedState(leafWork.Achievement) || node.ArchivedAt is not null) {
			throw new InvariantViolationException(
				"work-session-leaf-closed", "This leaf is closed to new sessions (terminal achievement or archived).");
		}

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
		catch (Exception ex) when (FindLeafClosedViolation(ex) is not null) {
			throw new InvariantViolationException(
				"work-session-leaf-closed", "This leaf is closed to new sessions (terminal achievement or archived).", ex);
		}
		catch (Exception ex) when (FindOverlapException(ex) is not null) {
			throw new InvariantViolationException(
				"work-session-overlap", "This session would overlap another session for the same worker and leaf.", ex);
		}

		return ToResult(session);
	}

	/// <inheritdoc />
	public async Task<WorkSessionResult> FinishSessionAsync(FinishSessionRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		var session = await LoadTrackedSessionAsync(context, request.SessionId, cancellationToken).ConfigureAwait(false);
		EnsureLeafMatchesOrThrow(session, request.LeafWorkId);
		await AuthorizeFinishOrThrowAsync(
			context, request.Context.Actor, session.LeafWorkId, session.WorkedByUserId, now, cancellationToken).ConfigureAwait(false);
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
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		var session = await LoadTrackedSessionAsync(context, request.SessionId, cancellationToken).ConfigureAwait(false);
		EnsureLeafMatchesOrThrow(session, request.LeafWorkId);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, session.LeafWorkId, now, cancellationToken).ConfigureAwait(false);
		CheckVersionOrThrow(session.RowVersion, request.Version);

		if (request.FinishedAt is Instant finishedAt && finishedAt <= request.StartedAt) {
			throw new InvariantViolationException(
				"work-session-invalid-interval", "A session's finish instant must be after its start instant.");
		}

		if (request.FinishedAt is null
			&& await LeafSessionClosure.IsClosedAsync(context, session.LeafWorkId, cancellationToken).ConfigureAwait(false)) {
			throw new InvariantViolationException(
				"work-session-leaf-closed",
				"This correction would leave the session active on a closed leaf. Use \"Reopen and start session\" on the leaf's Work page instead.");
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
		catch (Exception ex) when (FindLeafClosedViolation(ex) is not null) {
			throw new InvariantViolationException(
				"work-session-leaf-closed",
				"This correction would leave the session active on a closed leaf. Use \"Reopen and start session\" on the leaf's Work page instead.",
				ex);
		}
		catch (Exception ex) when (FindOverlapException(ex) is not null) {
			throw new InvariantViolationException(
				"work-session-overlap", "This correction would overlap another session for the same worker and leaf.", ex);
		}

		return ToResult(session);
	}

	/// <inheritdoc />
	public async Task<CompleteLeafResult> CompleteLeafAsync(CompleteLeafRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var leafWork = await context.Set<LeafWorkEntity>()
						   .FirstOrDefaultAsync(lw => lw.JobNodeId == request.JobNodeId, cancellationToken).ConfigureAwait(false)
					   ?? throw new EntityNotFoundException($"Job node {request.JobNodeId} has no LeafWork attached.");

		var now = clock.GetCurrentInstant();
		await AuthorizeCompleteOrThrowAsync(context, request.Context.Actor, request.JobNodeId, now, cancellationToken).ConfigureAwait(false);
		CheckVersionOrThrow(leafWork.RowVersion, request.Version);

		if (!AchievementTransitions.IsPermitted(leafWork.Achievement, request.FinalAchievement)) {
			throw new InvariantViolationException(
				"achievement-transition-not-permitted", $"Cannot transition from {leafWork.Achievement} to {request.FinalAchievement}.");
		}

		var activeSessions = await context.Set<WorkSessionEntity>()
			.Where(s => s.LeafWorkId == request.JobNodeId && s.FinishedAt == null)
			.OrderBy(s => s.Id)
			.ToListAsync(cancellationToken).ConfigureAwait(false);
		var expected = request.ExpectedActiveSessions.OrderBy(e => e.Id.Value).ToList();
		var matchesExpected = activeSessions.Count == expected.Count
							  && activeSessions.Zip(expected)
								  .All(pair => pair.First.Id == pair.Second.Id && pair.First.RowVersion == pair.Second.Version);
		if (!matchesExpected) {
			throw new ConcurrencyConflictException("The leaf's current active-session set no longer matches the confirmed set.");
		}

		var finishedAt = request.FinishedAt ?? now;
		if (activeSessions.Any(s => finishedAt <= s.StartedAt)) {
			throw new InvariantViolationException(
				"work-session-invalid-interval", "A session's finish instant must be after its start instant.");
		}

		if (finishedAt > now) {
			throw new InvariantViolationException(
				"work-session-finish-in-future", "A session's finish instant must not be in the future.");
		}

		if (!await LeafReadiness.IsReadyAsync(context, request.JobNodeId, cancellationToken).ConfigureAwait(false)) {
			throw new PrerequisiteBlockedException($"Job node {request.JobNodeId}'s prerequisites are not satisfied.");
		}

		foreach (var session in activeSessions) {
			session.FinishedAt = finishedAt;
			session.ChangedAt = now;
			session.RowVersion += 1;

			AuditEventWriter.Add(
				context, request.Context.Actor, now, "finish-work-session", "work_session", session.Id.Value, request.Context.CorrelationId,
				null,
				new Dictionary<string, string?> { ["finished_at"] = null },
				new Dictionary<string, string?> { ["finished_at"] = session.FinishedAt?.ToString() });
		}

		try {
			// SQLite's leaf-closure immediate trigger (unlike PostgreSQL's deferred one) evaluates the
			// moment each statement runs, not at commit -- the session finish rows must actually commit
			// to the table before the achievement UPDATE runs, or the trigger still sees an active
			// session. Two SaveChangesAsync calls inside the one open transaction preserve atomicity
			// (a failure on either still rolls back the whole transaction) while giving the trigger the
			// write order it needs.
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			var previousAchievement = leafWork.Achievement;
			leafWork.Achievement = request.FinalAchievement;
			leafWork.ChangedAt = now;
			leafWork.RowVersion += 1;

			var completionReason = request.CompletionNote is { Length: > 0 } note
				? $"{CompletionReason} ({note})"
				: CompletionReason;
			AuditEventWriter.Add(
				context, request.Context.Actor, now, "set-achievement", "leaf_work", leafWork.JobNodeId.Value, request.Context.CorrelationId,
				completionReason,
				new Dictionary<string, string?> { ["achievement"] = previousAchievement.ToString() },
				new Dictionary<string, string?> { ["achievement"] = leafWork.Achievement.ToString() });

			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException ex) {
			throw new ConcurrencyConflictException(
				$"Expected version for job node {request.JobNodeId} or one of its active sessions did not match its current version.", ex);
		}
		catch (Exception ex) when (FindActiveSessionsViolation(ex) is not null) {
			throw new InvariantViolationException(
				"leaf-closure-active-sessions", "This leaf cannot transition to a terminal achievement while a session is active.", ex);
		}

		return new() {
			JobNodeId = request.JobNodeId,
			Achievement = leafWork.Achievement,
			ChangedAt = leafWork.ChangedAt,
			Version = leafWork.RowVersion,
			FinishedSessions = [.. activeSessions.Select(ToResult)],
		};
	}

	/// <inheritdoc />
	public async Task<ReopenAndStartWorkResult> ReopenAndStartWorkAsync(
		ReopenAndStartWorkRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason, nameof(request.Reason));

		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var leafWork = await context.Set<LeafWorkEntity>()
						   .FirstOrDefaultAsync(lw => lw.JobNodeId == request.JobNodeId, cancellationToken).ConfigureAwait(false)
					   ?? throw new EntityNotFoundException($"Job node {request.JobNodeId} has no LeafWork attached.");
		var node = await context.Set<JobNodeEntity>().AsNoTracking()
					   .FirstOrDefaultAsync(n => n.Id == request.JobNodeId, cancellationToken).ConfigureAwait(false)
				   ?? throw new EntityNotFoundException($"Job node {request.JobNodeId} does not exist.");

		var now = clock.GetCurrentInstant();
		CheckVersionOrThrow(leafWork.RowVersion, request.Version);

		if (!AchievementTransitions.IsPermitted(leafWork.Achievement, Achievement.Waiting)) {
			throw new InvariantViolationException(
				"achievement-transition-not-permitted", $"Cannot reopen from {leafWork.Achievement}.");
		}

		if (node.ArchivedAt is not null) {
			throw new InvariantViolationException(
				"work-session-leaf-closed", "An archived node's leaf must be restored before it can be reopened.");
		}

		await AutoClaimUnassignedNodeAsync(context, request.Context, request.JobNodeId, request.WorkedByUserId, now, cancellationToken)
			.ConfigureAwait(false);
		await AuthorizeReopenAndStartOrThrowAsync(
			context, request.Context.Actor, request.JobNodeId, request.WorkedByUserId, now, cancellationToken).ConfigureAwait(false);
		await EnsureTargetWorkerEligibleAsync(context, request.Context.Actor, request.WorkedByUserId, now, cancellationToken)
			.ConfigureAwait(false);

		if (!await LeafReadiness.IsReadyAsync(context, request.JobNodeId, cancellationToken).ConfigureAwait(false)) {
			throw new PrerequisiteBlockedException($"Job node {request.JobNodeId}'s prerequisites are not satisfied.");
		}

		if (leafWork.Achievement == Achievement.Success &&
			await PrerequisiteReadinessSerialization.HasActiveDependentWorkAsync(context, request.JobNodeId, cancellationToken)
				.ConfigureAwait(false)) {
			throw new ConcurrencyConflictException(
				$"Job node {request.JobNodeId} cannot be reopened because dependent work became active.");
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

		var previousAchievement = leafWork.Achievement;
		leafWork.Achievement = Achievement.Waiting;
		leafWork.ChangedAt = now;
		leafWork.RowVersion += 1;

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "set-achievement", "leaf_work", leafWork.JobNodeId.Value, request.Context.CorrelationId,
			request.Reason,
			new Dictionary<string, string?> { ["achievement"] = previousAchievement.ToString() },
			new Dictionary<string, string?> { ["achievement"] = leafWork.Achievement.ToString() });

		var reopenedAchievement = leafWork.Achievement;
		leafWork.Achievement = Achievement.InProgress;
		leafWork.RowVersion += 1;

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "set-achievement", "leaf_work", leafWork.JobNodeId.Value, request.Context.CorrelationId,
			AutoAdvanceReason,
			new Dictionary<string, string?> { ["achievement"] = reopenedAchievement.ToString() },
			new Dictionary<string, string?> { ["achievement"] = leafWork.Achievement.ToString() });

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
		catch (DbUpdateConcurrencyException ex) {
			throw new ConcurrencyConflictException(
				$"Expected version {request.Version} for job node {request.JobNodeId} did not match its current version.", ex);
		}
		catch (Exception ex) when (FindActiveSessionUniqueViolation(ex) is not null) {
			throw new InvariantViolationException(
				"work-session-already-active", "This worker already has an active session for this leaf.", ex);
		}
		catch (Exception ex) when (FindLeafClosedViolation(ex) is not null) {
			throw new InvariantViolationException(
				"work-session-leaf-closed", "This leaf is closed to new sessions (terminal achievement or archived).", ex);
		}
		catch (Exception ex) when (FindOverlapException(ex) is not null) {
			throw new InvariantViolationException(
				"work-session-overlap", "This session would overlap another session for the same worker and leaf.", ex);
		}

		return new() {
			JobNodeId = request.JobNodeId,
			Achievement = leafWork.Achievement,
			ChangedAt = leafWork.ChangedAt,
			Version = leafWork.RowVersion,
			Session = ToResult(session),
		};
	}

	/// <summary>
	///     ADR 0045 §3.6: <see cref="CompleteLeafAsync" /> requires the same authority
	///     <see cref="SqliteAchievementCommandPort.SetAchievementAsync" /> already requires for the
	///     terminal transition -- controlling owner, Job Manager, or Administrator, never the narrower
	///     self-finish exception <see cref="WorkSessionAccessPolicy.CanFinishSession" /> adds for pausing.
	/// </summary>
	private static async Task AuthorizeCompleteOrThrowAsync(
		SqliteJobTrackDbContext context, AppUserId actorId, JobNodeId leafId, Instant now, CancellationToken cancellationToken)
	{
		var actorRoles = await GetActorRolesAsync(context, actorId, now, cancellationToken).ConfigureAwait(false);
		var ancestorOwnerIds = await JobNodeHierarchyQueries.GetAncestorOwnerIdsAsync(context, leafId.Value, cancellationToken)
			.ConfigureAwait(false);

		if (!AchievementAccessPolicy.CanSetAchievement(actorRoles, ancestorOwnerIds.Contains(actorId.Value), false)) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not complete job node {leafId}.");
		}
	}

	/// <summary>ADR 0045 §2: the atomic reopen-and-start composite's own, wider authority test.</summary>
	private static async Task AuthorizeReopenAndStartOrThrowAsync(
		SqliteJobTrackDbContext context, AppUserId actorId, JobNodeId leafId, AppUserId targetWorkedByUserId, Instant now,
		CancellationToken cancellationToken)
	{
		var actorRoles = await GetActorRolesAsync(context, actorId, now, cancellationToken).ConfigureAwait(false);
		var ancestorOwnerIds = await JobNodeHierarchyQueries.GetAncestorOwnerIdsAsync(context, leafId.Value, cancellationToken)
			.ConfigureAwait(false);
		var actorParticipatedPreviously = await context.Set<WorkSessionEntity>().AsNoTracking()
			.AnyAsync(s => s.LeafWorkId == leafId && s.WorkedByUserId == actorId, cancellationToken).ConfigureAwait(false);

		if (!LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
				actorRoles, ancestorOwnerIds.Contains(actorId.Value), actorParticipatedPreviously, actorId, targetWorkedByUserId)) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not reopen and start job node {leafId} for {targetWorkedByUserId}.");
		}
	}

	/// <summary>
	///     ADR 0044 Stage 6: the same <see cref="ActiveSessionsMessage" /> backstop
	///     <see cref="SqliteAchievementCommandPort" /> uses for an ordinary <c>SetAchievementAsync</c>
	///     terminal transition.
	/// </summary>
	private static SqliteException? FindActiveSessionsViolation(Exception? ex) =>
		ex switch {
			null => null,
			SqliteException sqlite when sqlite.Message.Contains(ActiveSessionsMessage, StringComparison.Ordinal) => sqlite,
			_ => FindActiveSessionsViolation(ex.InnerException),
		};

	/// <summary>
	///     A tracked-entity <c>SaveChangesAsync</c> (unlike <c>SqliteJobNodeCommandPort</c>'s raw
	///     <c>ExecuteSqlInterpolatedAsync</c> calls) wraps the driver's <see cref="SqliteException" />
	///     inside a <see cref="DbUpdateException" />, so this walks the whole
	///     <see cref="Exception.InnerException" /> chain rather than checking one level.
	/// </summary>
	private static SqliteException? FindOverlapException(Exception? ex) =>
		ex switch {
			null => null,
			SqliteException sqlite when sqlite.SqliteErrorCode == SqliteConstraintErrorCode => sqlite,
			_ => FindOverlapException(ex.InnerException),
		};

	/// <summary>
	///     Unlike <see cref="FindOverlapException" /> (used where only one message is ever meaningful at
	///     the call site), a backdated <see cref="StartSessionRequest.StartedAt" /> can plausibly hit
	///     either schema version 0007 constraint, so <see cref="StartSessionAsync" /> disambiguates: this
	///     checks the extended error code for the unique-index violation specifically ("already active");
	///     anything else matching <see cref="FindOverlapException" />'s broader base-code check is the
	///     trigger-based general-overlap violation instead.
	/// </summary>
	private static SqliteException? FindActiveSessionUniqueViolation(Exception? ex) =>
		ex switch {
			null => null,
			SqliteException sqlite when sqlite.SqliteExtendedErrorCode == SqliteUniqueConstraintErrorCode => sqlite,
			_ => FindActiveSessionUniqueViolation(ex.InnerException),
		};

	/// <summary>
	///     ADR 0044's <see cref="LeafClosedMessage" /> backstop: SQLite gives triggers no distinct
	///     extended error code, so this walks the chain checking message content instead of an error
	///     code, unlike <see cref="FindActiveSessionUniqueViolation" />/<see cref="FindOverlapException" />.
	/// </summary>
	private static SqliteException? FindLeafClosedViolation(Exception? ex) =>
		ex switch {
			null => null,
			SqliteException sqlite when sqlite.Message.Contains(LeafClosedMessage, StringComparison.Ordinal) => sqlite,
			_ => FindLeafClosedViolation(ex.InnerException),
		};

	private Task<SqliteJobTrackDbContext> CreateOpenContextAsync(CancellationToken cancellationToken) =>
		SqliteDbContextFactory.CreateOpenContextAsync(connectionString, cancellationToken);

	private static async Task<WorkSessionEntity> LoadTrackedSessionAsync(
		SqliteJobTrackDbContext context, WorkSessionId sessionId, CancellationToken cancellationToken) =>
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
	///     ADR 0048: starting a session on an unassigned node claims it for
	///     <paramref name="workedByUserId" /> -- the same conditional, race-safe write
	///     <see cref="SqliteJobNodeCommandPort.PickUpAsync" /> uses, gated by the identical
	///     <see cref="JobPickupPolicy.CanPickUp" /> eligibility test -- immediately before the caller
	///     runs its own <see cref="AuthorizeOrThrowAsync" />/<see cref="AuthorizeReopenAndStartOrThrowAsync" />
	///     check against the node's now-current ownership. A no-op for an already-owned node or an
	///     actor ineligible even to pick up, leaving the existing <c>canRecordWork</c> denial to fire.
	/// </summary>
	private static async Task AutoClaimUnassignedNodeAsync(
		SqliteJobTrackDbContext context, CommandContext ctx, JobNodeId nodeId, AppUserId workedByUserId, Instant now,
		CancellationToken cancellationToken)
	{
		var isUnassigned = await context.Set<JobNodeEntity>().AsNoTracking()
			.Where(n => n.Id == nodeId).Select(n => n.OwnerUserId == null).SingleAsync(cancellationToken).ConfigureAwait(false);
		if (!isUnassigned) {
			return;
		}

		var actorRoles = await GetActorRolesAsync(context, ctx.Actor, now, cancellationToken).ConfigureAwait(false);
		if (!JobPickupPolicy.CanPickUp(actorRoles, true)) {
			return;
		}

		// The conditional WHERE owner_user_id IS NULL is the concurrency mechanism itself; SQLite's
		// BEGIN IMMEDIATE (started by each caller's own transaction) serializes concurrent writes, so
		// a concurrent claimant that commits first leaves zero rows affected here, identical to
		// PickUpAsync's own race-safety guarantee.
		var affected = await context.Database.ExecuteSqlInterpolatedAsync(
			$"""
			 UPDATE job_node SET owner_user_id = {workedByUserId.Value}, row_version = row_version + 1
			 WHERE id = {nodeId.Value} AND owner_user_id IS NULL;
			 """, cancellationToken).ConfigureAwait(false);

		if (affected == 0) {
			throw new InvariantViolationException("job-node-already-claimed", $"Job node {nodeId} has already been claimed.");
		}

		AuditEventWriter.Add(
			context, ctx.Actor, now, "pick-up-job-node", "job_node", nodeId.Value, ctx.CorrelationId, AutoClaimReason,
			new Dictionary<string, string?> { ["owner_user_id"] = null },
			new Dictionary<string, string?> { ["owner_user_id"] = workedByUserId.Value.ToString(CultureInfo.InvariantCulture) });
	}

	/// <summary>
	///     Owner-gated recording (ownership model §4.2; ADR 0032): the actor may manage a session on
	///     <paramref name="leafId" /> if they control it -- directly own it or an ancestor, reusing the
	///     same ancestor-owner walk <c>SqliteJobNodeCommandPort</c> performs for structural commands
	///     (impl plan's risk note: the two ports must compute the same control set, not duplicate the
	///     walk divergently) -- or hold Administrator/JobManager.
	/// </summary>
	private static async Task AuthorizeOrThrowAsync(
		SqliteJobTrackDbContext context, AppUserId actorId, JobNodeId leafId, Instant now, CancellationToken cancellationToken)
	{
		var actorRoles = await GetActorRolesAsync(context, actorId, now, cancellationToken).ConfigureAwait(false);
		var ancestorOwnerIds = await JobNodeHierarchyQueries.GetAncestorOwnerIdsAsync(context, leafId.Value, cancellationToken)
			.ConfigureAwait(false);

		if (!WorkSessionAccessPolicy.CanManage(actorRoles, ancestorOwnerIds.Contains(actorId.Value))) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not manage a session on job node {leafId}.");
		}
	}

	/// <summary>
	///     ADR 0045 §5: finishing a session admits one narrow exception beyond <see cref="AuthorizeOrThrowAsync" />'s
	///     node-control rule -- the worker named on the session may always finish it themselves, even
	///     after node ownership changed out from under them post-start. Governs <see cref="FinishSessionAsync" />
	///     only; <see cref="StartSessionAsync" />/<see cref="StartWorkAsync" />/<see cref="CorrectSessionAsync" />
	///     keep the unqualified node-control rule via <see cref="AuthorizeOrThrowAsync" />.
	/// </summary>
	private static async Task AuthorizeFinishOrThrowAsync(
		SqliteJobTrackDbContext context, AppUserId actorId, JobNodeId leafId, AppUserId sessionWorkedByUserId, Instant now,
		CancellationToken cancellationToken)
	{
		var actorRoles = await GetActorRolesAsync(context, actorId, now, cancellationToken).ConfigureAwait(false);
		var ancestorOwnerIds = await JobNodeHierarchyQueries.GetAncestorOwnerIdsAsync(context, leafId.Value, cancellationToken)
			.ConfigureAwait(false);

		if (!WorkSessionAccessPolicy.CanFinishSession(
				actorRoles, ancestorOwnerIds.Contains(actorId.Value), actorId == sessionWorkedByUserId)) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not finish this session on job node {leafId}.");
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

	/// <summary>
	///     ADR 0044 Stage 6/plan §2.5 rule 6: starting a session for a worker other than the actor
	///     (the "Start for…" disclosure) must re-validate the target at write time, not merely trust
	///     the picker's render-time snapshot -- a target disabled, locked, or role-revoked since the
	///     page was rendered is rejected here rather than silently starting a session for them anyway.
	///     A no-op when the target is the actor themselves, since <see cref="GetActorRolesAsync" />
	///     already re-validated that account.
	/// </summary>
	private static async Task EnsureTargetWorkerEligibleAsync(
		SqliteJobTrackDbContext context, AppUserId actorId, AppUserId targetId, Instant now, CancellationToken cancellationToken)
	{
		if (targetId == actorId) {
			return;
		}

		var targetIdentityUser = await context.Set<IdentityUserEntity>().AsNoTracking()
									 .FirstOrDefaultAsync(iu => iu.AppUserId == targetId, cancellationToken).ConfigureAwait(false)
								 ?? throw new EntityNotFoundException($"Worker {targetId} does not exist.");

		if (!targetIdentityUser.IsEnabled
			|| (targetIdentityUser.LockoutEnabled && targetIdentityUser.LockoutEnd is Instant lockoutEnd && lockoutEnd > now)) {
			throw new InvariantViolationException(
				"work-session-target-not-eligible", $"Worker {targetId} is disabled and cannot be started for.");
		}

		var targetRoles = await context.Set<IdentityUserRoleEntity>().AsNoTracking()
			.Where(ur => ur.IdentityUserId == targetIdentityUser.Id)
			.Select(ur => (EmployeeRole)ur.IdentityRoleId)
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);

		if (!targetRoles.Any(role => role is EmployeeRole.Administrator or EmployeeRole.JobManager or EmployeeRole.Worker)) {
			throw new InvariantViolationException(
				"work-session-target-not-eligible", $"Worker {targetId} is not an eligible workflow employee.");
		}
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
