namespace JobTrack.Persistence.Shared.Ports;

using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using Domain.Authorization;
using Domain.Hierarchy;
using Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

/// <summary>
///     The provider-neutral body of <see cref="IWorkSessionCommandPort" /> (impl plan §7.3 slice 6:
///     start, finish, resume, and correct work sessions). One context/connection/transaction per call
///     over <see cref="IProviderWriteOperations" />, reloading the actor's current roles and whether the
///     session is (or will be) their own and applying <see cref="WorkSessionAccessPolicy" /> itself
///     inside that transaction, per the port's own contract. Same-user/same-leaf overlap is enforced
///     purely by schema version 0007's own database constraints, so a concurrent conflict is caught by
///     classifying the driver exception with
///     <see cref="IProviderWriteOperations.ClassifyWriteConflict" />, not by taking a lock.
///     Closed-leaf session creation (ADR 0044) is the one exception: on PostgreSQL it uses ADR 0012's
///     "leaf session closure" advisory-lock domain, taken by schema version 0007's own deferred
///     constraint triggers (not by this port directly) to serialize against a concurrent terminal
///     achievement transition or archive on the same leaf, and
///     <see cref="IProviderWriteOperations.IsLeafReadyAsync" /> reuses that same per-leaf lock for each
///     required job, so reopening a formerly successful prerequisite cannot commit from the same
///     readiness snapshot as a dependent session start. SQLite needs none of it: its write transaction
///     already serializes every writer.
/// </summary>
internal sealed class WorkSessionCommandPort(IProviderWriteOperations provider, IClock clock) : IWorkSessionCommandPort
{
	/// <summary>ADR 0045 §4: the fixed structured reason recorded for every <see cref="CompleteLeafAsync" /> completion.</summary>
	private const string CompletionReason = "Completed from the leaf work page";

	/// <summary>ADR 0048's fixed audit reason for a session-start-triggered pickup, distinguishing it from an explicit one.</summary>
	private const string AutoClaimReason = "Automatically claimed on session start";

	/// <inheritdoc />
	public async Task<WorkSessionResult> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await provider.CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await provider.BeginWriteTransactionAsync(context, cancellationToken).ConfigureAwait(false);

		if (!await context.Set<LeafWorkEntity>().AsNoTracking()
				.AnyAsync(lw => lw.JobNodeId == request.LeafWorkId, cancellationToken).ConfigureAwait(false)) {
			throw new EntityNotFoundException($"Job node {request.LeafWorkId} has no LeafWork attached.");
		}

		var now = clock.GetCurrentInstant();
		await AutoClaimUnassignedNodeAsync(context, request.Context, request.LeafWorkId, request.WorkedByUserId, now, cancellationToken)
			.ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.LeafWorkId, now, cancellationToken).ConfigureAwait(false);
		await EnsureTargetWorkerEligibleAsync(context, request.WorkedByUserId, now, cancellationToken)
			.ConfigureAwait(false);

		if (await LeafSessionClosure.IsClosedAsync(context, request.LeafWorkId, cancellationToken).ConfigureAwait(false)) {
			throw new InvariantViolationException(
				"work-session-leaf-closed", "This leaf is closed to new sessions (terminal achievement or archived).");
		}

		if (!await provider.IsLeafReadyAsync(context, request.LeafWorkId, null, cancellationToken).ConfigureAwait(false)) {
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
		catch (Exception ex) when (provider.ClassifyWriteConflict(ex) is WriteConflictKind.UniquenessViolation) {
			throw new InvariantViolationException(
				"work-session-already-active", "This worker already has an active session for this leaf.", ex);
		}
		catch (Exception ex) when (provider.ClassifyWriteConflict(ex) is WriteConflictKind.LeafClosed) {
			throw new InvariantViolationException(
				"work-session-leaf-closed", "This leaf is closed to new sessions (terminal achievement or archived).", ex);
		}
		catch (Exception ex) when (provider.ClassifyWriteConflict(ex) is WriteConflictKind.RangeOverlap) {
			throw new InvariantViolationException(
				"work-session-overlap", "This session would overlap another session for the same worker and leaf.", ex);
		}

		return ToResult(session);
	}

	/// <inheritdoc />
	public async Task<WorkSessionResult> StartWorkAsync(StartWorkRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await provider.CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await provider.BeginWriteTransactionAsync(context, cancellationToken).ConfigureAwait(false);

		var node = await context.Set<JobNodeEntity>().AsNoTracking()
					   .FirstOrDefaultAsync(n => n.Id == request.JobNodeId, cancellationToken).ConfigureAwait(false)
				   ?? throw new EntityNotFoundException($"Job node {request.JobNodeId} does not exist.");
		var now = clock.GetCurrentInstant();
		await AutoClaimUnassignedNodeAsync(context, request.Context, request.JobNodeId, request.WorkedByUserId, now, cancellationToken)
			.ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.JobNodeId, now, cancellationToken).ConfigureAwait(false);
		await EnsureTargetWorkerEligibleAsync(context, request.WorkedByUserId, now, cancellationToken)
			.ConfigureAwait(false);

		var leafWork = await context.Set<LeafWorkEntity>()
			.FirstOrDefaultAsync(lw => lw.JobNodeId == request.JobNodeId, cancellationToken).ConfigureAwait(false);
		leafWork ??= await LeafWorkAttachSupport.CreateAsync(
			context, node, now, request.Context, null, null, cancellationToken).ConfigureAwait(false);

		if (AchievementTransitions.IsCompletedState(leafWork.Achievement) || node.ArchivedAt is not null) {
			throw new InvariantViolationException(
				"work-session-leaf-closed", "This leaf is closed to new sessions (terminal achievement or archived).");
		}

		if (!await provider.IsLeafReadyAsync(context, request.JobNodeId, null, cancellationToken).ConfigureAwait(false)) {
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
			await LeafAchievementTransition.ApplyAsync(
					context, leafWork, Achievement.InProgress, request.Context.Actor, now, request.Context.CorrelationId,
					WorkAuditReasons.AutoAdvancedOnSessionStart, cancellationToken)
				.ConfigureAwait(false);
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
		catch (Exception ex) when (provider.ClassifyWriteConflict(ex) is WriteConflictKind.UniquenessViolation) {
			throw new InvariantViolationException(
				"work-session-already-active", "This worker already has an active session for this leaf.", ex);
		}
		catch (Exception ex) when (provider.ClassifyWriteConflict(ex) is WriteConflictKind.LeafClosed) {
			throw new InvariantViolationException(
				"work-session-leaf-closed", "This leaf is closed to new sessions (terminal achievement or archived).", ex);
		}
		catch (Exception ex) when (provider.ClassifyWriteConflict(ex) is WriteConflictKind.RangeOverlap) {
			throw new InvariantViolationException(
				"work-session-overlap", "This session would overlap another session for the same worker and leaf.", ex);
		}

		return ToResult(session);
	}

	/// <inheritdoc />
	public async Task<WorkSessionResult> FinishSessionAsync(FinishSessionRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await provider.CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await provider.BeginWriteTransactionAsync(context, cancellationToken).ConfigureAwait(false);

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
	public async Task<FinishSessionAndUpdateWriteUpResult> FinishSessionAndUpdateWriteUpAsync(
		FinishSessionAndUpdateWriteUpRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await provider.CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await provider.BeginWriteTransactionAsync(context, cancellationToken).ConfigureAwait(false);

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

		var writeUpChanged = false;
		JobNodeEntity? writtenUpNode = null;
		if (request.WriteUpChange is WriteUpChange writeUpChange) {
			// Same node-control authority EditAsync's own JobNodeAccessPolicy.CanManage would require --
			// distinct from AuthorizeFinishOrThrowAsync's session-owner exception above, which governs
			// finishing the session itself, not editing the node's write-up.
			await AuthorizeOrThrowAsync(context, request.Context.Actor, session.LeafWorkId, now, cancellationToken).ConfigureAwait(false);
			(writeUpChanged, writtenUpNode) = await WriteUpChangeApplier.ApplyAsync(
				context, session.LeafWorkId, writeUpChange.NodeVersion, writeUpChange.WriteUp, request.Context.Actor,
				request.Context.CorrelationId, now, cancellationToken).ConfigureAwait(false);
		}

		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException ex) {
			throw new ConcurrencyConflictException(
				$"Expected version {request.Version} for work session {request.SessionId} did not match its current version.", ex);
		}

		return new() {
			Session = ToResult(session),
			WriteUpChanged = writeUpChanged,
			Node = writtenUpNode is null
				? null
				: await JobNodeStructuralProjection.ToResultAsync(context, writtenUpNode, cancellationToken)
					.ConfigureAwait(false),
		};
	}

	/// <inheritdoc />
	public async Task<WorkSessionResult> CorrectSessionAsync(CorrectSessionRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await provider.CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await provider.BeginWriteTransactionAsync(context, cancellationToken).ConfigureAwait(false);

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
		catch (Exception ex) when (provider.ClassifyWriteConflict(ex) is WriteConflictKind.LeafClosed) {
			throw new InvariantViolationException(
				"work-session-leaf-closed",
				"This correction would leave the session active on a closed leaf. Use \"Reopen and start session\" on the leaf's Work page instead.",
				ex);
		}
		catch (Exception ex) when (provider.ClassifyWriteConflict(ex) is WriteConflictKind.RangeOverlap or WriteConflictKind.UniquenessViolation) {
			throw new InvariantViolationException(
				"work-session-overlap", "This correction would overlap another session for the same worker and leaf.", ex);
		}

		return ToResult(session);
	}

	/// <inheritdoc />
	public async Task<CompleteLeafResult> CompleteLeafAsync(CompleteLeafRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await provider.CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await provider.BeginWriteTransactionAsync(context, cancellationToken).ConfigureAwait(false);

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

		var activeSessions = await LoadConfirmedActiveSessionsAsync(
			context, request.JobNodeId, request.ExpectedActiveSessions, cancellationToken).ConfigureAwait(false);

		var finishedAt = request.FinishedAt ?? now;
		EnsureFinishInstantValid(activeSessions, finishedAt, now);

		if (!await provider.IsLeafReadyAsync(context, request.JobNodeId, null, cancellationToken).ConfigureAwait(false)) {
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

		var writeUpChanged = false;
		JobNodeEntity? writtenUpNode = null;

		try {
			// The session-finish rows must reach the table before the achievement transition where the
			// leaf-closure trigger is immediate rather than deferred to commit; both writes still commit
			// or roll back together, since this stays inside the one open transaction.
			await provider.FlushBeforeTerminalTransitionAsync(context, cancellationToken).ConfigureAwait(false);

			var completionReason = request.CompletionNote is { Length: > 0 } note
				? $"{CompletionReason} ({note})"
				: CompletionReason;
			await LeafAchievementTransition.ApplyAsync(
					context, leafWork, request.FinalAchievement, request.Context.Actor, now, request.Context.CorrelationId, completionReason,
					cancellationToken)
				.ConfigureAwait(false);

			if (request.WriteUpChange is WriteUpChange writeUpChange) {
				(writeUpChanged, writtenUpNode) = await WriteUpChangeApplier.ApplyAsync(
					context, request.JobNodeId, writeUpChange.NodeVersion, writeUpChange.WriteUp, request.Context.Actor,
					request.Context.CorrelationId, now, cancellationToken).ConfigureAwait(false);
			}

			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException ex) {
			throw new ConcurrencyConflictException(
				$"Expected version for job node {request.JobNodeId} or one of its active sessions did not match its current version.", ex);
		}
		catch (Exception ex) when (provider.ClassifyWriteConflict(ex) is WriteConflictKind.ActiveSessions) {
			throw new InvariantViolationException(
				"leaf-closure-active-sessions", "This leaf cannot transition to a terminal achievement while a session is active.", ex);
		}

		return new() {
			JobNodeId = request.JobNodeId,
			Achievement = leafWork.Achievement,
			ChangedAt = leafWork.ChangedAt,
			Version = leafWork.RowVersion,
			FinishedSessions = [.. activeSessions.Select(ToResult)],
			WriteUpChanged = writeUpChanged,
			Node = writtenUpNode is null
				? null
				: await JobNodeStructuralProjection.ToResultAsync(context, writtenUpNode, cancellationToken)
					.ConfigureAwait(false),
		};
	}

	/// <inheritdoc />
	public async Task<PauseLeafResult> PauseLeafAsync(PauseLeafRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await provider.CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await provider.BeginWriteTransactionAsync(context, cancellationToken).ConfigureAwait(false);

		if (!await context.Set<LeafWorkEntity>().AsNoTracking()
				.AnyAsync(lw => lw.JobNodeId == request.JobNodeId, cancellationToken).ConfigureAwait(false)) {
			throw new EntityNotFoundException($"Job node {request.JobNodeId} has no LeafWork attached.");
		}

		var now = clock.GetCurrentInstant();
		var activeSessions = await LoadConfirmedActiveSessionsAsync(
			context, request.JobNodeId, request.ExpectedActiveSessions, cancellationToken).ConfigureAwait(false);
		await AuthorizePauseOrThrowAsync(context, request.Context.Actor, request.JobNodeId, activeSessions, now, cancellationToken)
			.ConfigureAwait(false);

		var finishedAt = request.FinishedAt ?? now;
		EnsureFinishInstantValid(activeSessions, finishedAt, now);

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

		var writeUpChanged = false;
		JobNodeEntity? writtenUpNode = null;
		if (request.WriteUpChange is WriteUpChange writeUpChange) {
			// Same node-control authority EditAsync's own JobNodeAccessPolicy.CanManage would require --
			// distinct from the per-session finish authority above, which governs ending the sessions
			// themselves, not editing the node's write-up.
			await AuthorizeOrThrowAsync(context, request.Context.Actor, request.JobNodeId, now, cancellationToken).ConfigureAwait(false);
			(writeUpChanged, writtenUpNode) = await WriteUpChangeApplier.ApplyAsync(
				context, request.JobNodeId, writeUpChange.NodeVersion, writeUpChange.WriteUp, request.Context.Actor,
				request.Context.CorrelationId, now, cancellationToken).ConfigureAwait(false);
		}

		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException ex) {
			throw new ConcurrencyConflictException(
				$"Expected version for one of job node {request.JobNodeId}'s active sessions did not match its current version.", ex);
		}

		return new() {
			JobNodeId = request.JobNodeId,
			FinishedSessions = [.. activeSessions.Select(ToResult)],
			WriteUpChanged = writeUpChanged,
			Node = writtenUpNode is null
				? null
				: await JobNodeStructuralProjection.ToResultAsync(context, writtenUpNode, cancellationToken)
					.ConfigureAwait(false),
		};
	}

	/// <inheritdoc />
	public async Task<ReopenAndStartWorkResult> ReopenAndStartWorkAsync(
		ReopenAndStartWorkRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason, nameof(request.Reason));

		await using var context = await provider.CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await provider.BeginWriteTransactionAsync(context, cancellationToken).ConfigureAwait(false);

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
		await EnsureTargetWorkerEligibleAsync(context, request.WorkedByUserId, now, cancellationToken)
			.ConfigureAwait(false);

		if (!await provider.IsLeafReadyAsync(context, request.JobNodeId, request.JobNodeId, cancellationToken).ConfigureAwait(false)) {
			throw new PrerequisiteBlockedException($"Job node {request.JobNodeId}'s prerequisites are not satisfied.");
		}

		// ADR 0051: live work on a dependent never blocks this reopen. The regression it creates is
		// carried by the dependent -- which becomes blocked, and cannot reach a terminal achievement
		// until this prerequisite succeeds again -- not by refusing the actor who knows the leaf was
		// closed wrongly. Passing this node's own id as the readiness check's
		// additionallyLockedRequiredJobId above still orders this reopen against a concurrent dependent
		// completion -- by advisory lock on PostgreSQL, by write serialization on SQLite -- so exactly
		// one of the two sees the other's state.
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

		await LeafAchievementTransition.ApplyAsync(
				context, leafWork, Achievement.Waiting, request.Context.Actor, now, request.Context.CorrelationId, request.Reason,
				cancellationToken)
			.ConfigureAwait(false);
		await LeafAchievementTransition.ApplyAsync(
				context, leafWork, Achievement.InProgress, request.Context.Actor, now, request.Context.CorrelationId,
				WorkAuditReasons.AutoAdvancedOnSessionStart, cancellationToken)
			.ConfigureAwait(false);

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
		catch (Exception ex) when (provider.ClassifyWriteConflict(ex) is WriteConflictKind.UniquenessViolation) {
			throw new InvariantViolationException(
				"work-session-already-active", "This worker already has an active session for this leaf.", ex);
		}
		catch (Exception ex) when (provider.ClassifyWriteConflict(ex) is WriteConflictKind.LeafClosed) {
			throw new InvariantViolationException(
				"work-session-leaf-closed", "This leaf is closed to new sessions (terminal achievement or archived).", ex);
		}
		catch (Exception ex) when (provider.ClassifyWriteConflict(ex) is WriteConflictKind.RangeOverlap) {
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
	///     the provider achievement command port's <c>SetAchievementAsync</c> already requires for the
	///     terminal transition -- controlling owner, Job Manager, or Administrator, never the narrower
	///     self-finish exception <see cref="WorkSessionAccessPolicy.CanFinishSession" /> adds for pausing.
	/// </summary>
	private static async Task AuthorizeCompleteOrThrowAsync(
		DbContext context, AppUserId actorId, JobNodeId leafId, Instant now, CancellationToken cancellationToken)
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
		DbContext context, AppUserId actorId, JobNodeId leafId, AppUserId targetWorkedByUserId, Instant now,
		CancellationToken cancellationToken)
	{
		var actorRoles = await GetActorRolesAsync(context, actorId, now, cancellationToken).ConfigureAwait(false);
		var ancestorOwnerIds = await JobNodeHierarchyQueries.GetAncestorOwnerIdsAsync(context, leafId.Value, cancellationToken)
			.ConfigureAwait(false);
		var actorParticipatedPreviously = await context.Set<WorkSessionEntity>().AsNoTracking()
			.AnyAsync(s => s.LeafWorkId == leafId && s.WorkedByUserId == actorId, cancellationToken).ConfigureAwait(false);

		if (!LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
				actorRoles,
				new() {
					ActorControlsNode = ancestorOwnerIds.Contains(actorId.Value),
					ActorParticipatedPreviously = actorParticipatedPreviously,
					ActorUserId = actorId,
					TargetWorkedByUserId = targetWorkedByUserId,
				})) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not reopen and start job node {leafId} for {targetWorkedByUserId}.");
		}
	}

	private static async Task<WorkSessionEntity> LoadTrackedSessionAsync(
		DbContext context, WorkSessionId sessionId, CancellationToken cancellationToken) =>
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
	///     the provider job-node command port's <c>PickUpAsync</c> uses, gated by the identical
	///     <see cref="JobPickupPolicy.CanPickUp" /> eligibility test -- immediately before the caller
	///     runs its own <see cref="AuthorizeOrThrowAsync" />/<see cref="AuthorizeReopenAndStartOrThrowAsync" />
	///     check against the node's now-current ownership. A no-op for an already-owned node or an
	///     actor ineligible even to pick up, leaving the existing <c>canRecordWork</c> denial to fire.
	/// </summary>
	private static async Task AutoClaimUnassignedNodeAsync(
		DbContext context, CommandContext ctx, JobNodeId nodeId, AppUserId workedByUserId, Instant now,
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

		if (!await UnassignedNodeClaim.TryClaimAsync(context, nodeId, workedByUserId, cancellationToken).ConfigureAwait(false)) {
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
	///     same ancestor-owner walk the provider job-node command port performs for structural commands
	///     (impl plan's risk note: the two ports must compute the same control set, not duplicate the
	///     walk divergently) -- or hold Administrator/JobManager.
	/// </summary>
	/// <summary>
	///     The leaf's current active sessions, tracked and ordered by id, after verifying they are
	///     exactly <paramref name="expectedActiveSessions" /> by id and version (ADR 0045 §3). Shared by
	///     <see cref="CompleteLeafAsync" /> and <see cref="PauseLeafAsync" />, which differ only in what
	///     they do once the confirmed set is in hand.
	/// </summary>
	private static async Task<List<WorkSessionEntity>> LoadConfirmedActiveSessionsAsync(
		DbContext context, JobNodeId leafId, EquatableArray<ExpectedActiveSession> expectedActiveSessions,
		CancellationToken cancellationToken)
	{
		var activeSessions = await context.Set<WorkSessionEntity>()
			.Where(s => s.LeafWorkId == leafId && s.FinishedAt == null)
			.OrderBy(s => s.Id)
			.ToListAsync(cancellationToken).ConfigureAwait(false);
		var expected = expectedActiveSessions.OrderBy(e => e.Id.Value).ToList();
		var matchesExpected = activeSessions.Count == expected.Count
							  && activeSessions.Zip(expected)
								  .All(pair => pair.First.Id == pair.Second.Id && pair.First.RowVersion == pair.Second.Version);
		if (!matchesExpected) {
			throw new ConcurrencyConflictException("The leaf's current active-session set no longer matches the confirmed set.");
		}

		return activeSessions;
	}

	/// <summary>The one finish instant applied to a confirmed set must be after every start in it, and never in the future (ADR 0028).</summary>
	private static void EnsureFinishInstantValid(List<WorkSessionEntity> activeSessions, Instant finishedAt, Instant now)
	{
		if (activeSessions.Exists(s => finishedAt <= s.StartedAt)) {
			throw new InvariantViolationException(
				"work-session-invalid-interval", "A session's finish instant must be after its start instant.");
		}

		if (finishedAt > now) {
			throw new InvariantViolationException(
				"work-session-finish-in-future", "A session's finish instant must not be in the future.");
		}
	}

	/// <summary>
	///     Pausing a leaf ends every worker's session on it, so it needs the finish authority for each
	///     one -- <see cref="AuthorizeFinishOrThrowAsync" />'s rule applied per session, with the actor's
	///     roles and the node's ancestor owners read once. A worker with no node control may therefore
	///     pause a leaf only they are clocked onto, but not one someone else is also working.
	/// </summary>
	private static async Task AuthorizePauseOrThrowAsync(
		DbContext context, AppUserId actorId, JobNodeId leafId, List<WorkSessionEntity> activeSessions, Instant now,
		CancellationToken cancellationToken)
	{
		var actorRoles = await GetActorRolesAsync(context, actorId, now, cancellationToken).ConfigureAwait(false);
		var ancestorOwnerIds = await JobNodeHierarchyQueries.GetAncestorOwnerIdsAsync(context, leafId.Value, cancellationToken)
			.ConfigureAwait(false);
		var controlsNode = ancestorOwnerIds.Contains(actorId.Value);

		foreach (var session in activeSessions) {
			if (!WorkSessionAccessPolicy.CanFinishSession(actorRoles, controlsNode, actorId == session.WorkedByUserId)) {
				throw new AuthorizationDeniedException($"Actor {actorId} may not pause job node {leafId}.");
			}
		}
	}

	private static async Task AuthorizeOrThrowAsync(
		DbContext context, AppUserId actorId, JobNodeId leafId, Instant now, CancellationToken cancellationToken)
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
		DbContext context, AppUserId actorId, JobNodeId leafId, AppUserId sessionWorkedByUserId, Instant now,
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
		DbContext context, AppUserId actorId, Instant now, CancellationToken cancellationToken)
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
	///     The target is checked even when it is the actor: a Requester role is disqualifying when
	///     combined with a workflow role, while actor authorization evaluates the actor's authority.
	/// </summary>
	private static async Task EnsureTargetWorkerEligibleAsync(
		DbContext context, AppUserId targetId, Instant now, CancellationToken cancellationToken)
	{
		await WorkflowEmployeeEligibility.EnsureMayBeAssignedWorkAsync(
			context, targetId, now, "work-session-target-not-eligible", cancellationToken).ConfigureAwait(false);
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
