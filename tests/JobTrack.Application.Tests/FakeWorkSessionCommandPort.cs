namespace JobTrack.Application.Tests;

using Abstractions;
using Domain.Authorization;
using Domain.Hierarchy;
using NodaTime;
using Ports;

/// <summary>
///     An in-memory fake of <see cref="IWorkSessionCommandPort" /> for application-slice tests (plan
///     §7.3: "write application tests with fake ports, then provider conformance tests using real
///     databases"). Delegates leaf existence and prerequisite readiness to the shared
///     <see cref="FakeJobNodeCommandPort" /> graph, mirroring how a real implementation would query the
///     same underlying tables rather than maintaining separate state.
/// </summary>
internal sealed class FakeWorkSessionCommandPort(FakeJobNodeCommandPort nodePort) : IWorkSessionCommandPort
{
	private readonly Dictionary<WorkSessionId, WorkSessionResult> _sessions = [];
	private long _nextId = 1;

	public Instant NowToReturn { get; set; } = Instant.FromUtc(2026, 1, 1, 12, 0);

	public async Task<WorkSessionResult> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
	{
		_ = nodePort.FindLeafWork(request.LeafWorkId)
			?? throw new EntityNotFoundException($"Job node {request.LeafWorkId} has no LeafWork attached.");
		AuthorizeOrThrow(request.Context.Actor, request.LeafWorkId);

		if (_sessions.Values.Any(session =>
				session.LeafWorkId == request.LeafWorkId
				&& session.WorkedByUserId == request.WorkedByUserId
				&& session.FinishedAt is null)) {
			throw new InvariantViolationException(
				"work-session-already-active", "This worker already has an active session for this leaf.");
		}

		var startedAt = request.StartedAt ?? NowToReturn;
		if (startedAt > NowToReturn) {
			throw new InvariantViolationException(
				"work-session-start-in-future", "A session's start instant must not be in the future.");
		}

		if (_sessions.Values.Any(session =>
				session.LeafWorkId == request.LeafWorkId
				&& session.WorkedByUserId == request.WorkedByUserId
				&& RangesOverlap(startedAt, null, session.StartedAt, session.FinishedAt))) {
			throw new InvariantViolationException(
				"work-session-overlap", "This session would overlap another session for the same worker and leaf.");
		}

		var readinessInputs = await nodePort.GetReadinessInputsAsync(request.LeafWorkId, cancellationToken).ConfigureAwait(false);
		var readiness = ReadinessCalculator.IsReady(request.LeafWorkId, readinessInputs.NodesById, readinessInputs.Prerequisites);
		if (!readiness.IsReady) {
			throw new PrerequisiteBlockedException($"Job node {request.LeafWorkId}'s prerequisites are not satisfied.");
		}

		var session = new WorkSessionResult {
			Id = new(_nextId++),
			LeafWorkId = request.LeafWorkId,
			WorkedByUserId = request.WorkedByUserId,
			StartedAt = startedAt,
			ChangedAt = NowToReturn,
			Version = 1,
		};
		_sessions[session.Id] = session;

		return session;
	}

	public async Task<WorkSessionResult> StartWorkAsync(StartWorkRequest request, CancellationToken cancellationToken = default)
	{
		var leafWork = nodePort.EnsureLeafWorkAttached(request.JobNodeId, request.Context.Actor);

		if (_sessions.Values.Any(session =>
				session.LeafWorkId == request.JobNodeId
				&& session.WorkedByUserId == request.WorkedByUserId
				&& session.FinishedAt is null)) {
			throw new InvariantViolationException(
				"work-session-already-active", "This worker already has an active session for this leaf.");
		}

		var startedAt = request.StartedAt ?? NowToReturn;
		if (startedAt > NowToReturn) {
			throw new InvariantViolationException(
				"work-session-start-in-future", "A session's start instant must not be in the future.");
		}

		if (_sessions.Values.Any(session =>
				session.LeafWorkId == request.JobNodeId
				&& session.WorkedByUserId == request.WorkedByUserId
				&& RangesOverlap(startedAt, null, session.StartedAt, session.FinishedAt))) {
			throw new InvariantViolationException(
				"work-session-overlap", "This session would overlap another session for the same worker and leaf.");
		}

		var readinessInputs = await nodePort.GetReadinessInputsAsync(request.JobNodeId, cancellationToken).ConfigureAwait(false);
		var readiness = ReadinessCalculator.IsReady(request.JobNodeId, readinessInputs.NodesById, readinessInputs.Prerequisites);
		if (!readiness.IsReady) {
			throw new PrerequisiteBlockedException($"Job node {request.JobNodeId}'s prerequisites are not satisfied.");
		}

		if (leafWork.Achievement == Achievement.Waiting) {
			nodePort.SetLeafWork(leafWork with { Achievement = Achievement.InProgress, ChangedAt = NowToReturn, Version = leafWork.Version + 1 });
		}

		var session = new WorkSessionResult {
			Id = new(_nextId++),
			LeafWorkId = request.JobNodeId,
			WorkedByUserId = request.WorkedByUserId,
			StartedAt = startedAt,
			ChangedAt = NowToReturn,
			Version = 1,
		};
		_sessions[session.Id] = session;

		return session;
	}

	public Task<WorkSessionResult> FinishSessionAsync(FinishSessionRequest request, CancellationToken cancellationToken = default)
	{
		var existing = GetExisting(request.SessionId);
		AuthorizeFinishOrThrow(request.Context.Actor, existing.LeafWorkId, existing.WorkedByUserId);
		CheckVersionOrThrow(existing.Version, request.Version);

		// Unlike a real clock, NowToReturn is a single frozen instant shared by every operation in a
		// test, so a defaulted (null) FinishedAt can legitimately equal StartedAt here without
		// indicating a real invalid interval; only a caller-supplied instant is validated.
		var finishedAt = request.FinishedAt ?? NowToReturn;
		if (request.FinishedAt is not null && finishedAt <= existing.StartedAt) {
			throw new InvariantViolationException(
				"work-session-invalid-interval", "A session's finish instant must be after its start instant.");
		}

		if (request.FinishedAt is not null && finishedAt > NowToReturn) {
			throw new InvariantViolationException(
				"work-session-finish-in-future", "A session's finish instant must not be in the future.");
		}

		var updated = existing with { FinishedAt = finishedAt, ChangedAt = NowToReturn, Version = existing.Version + 1 };
		_sessions[updated.Id] = updated;

		return Task.FromResult(updated);
	}

	public Task<WorkSessionResult> CorrectSessionAsync(CorrectSessionRequest request, CancellationToken cancellationToken = default)
	{
		var existing = GetExisting(request.SessionId);
		AuthorizeOrThrow(request.Context.Actor, existing.LeafWorkId);
		CheckVersionOrThrow(existing.Version, request.Version);

		if (request.FinishedAt is Instant finishedAt && finishedAt <= request.StartedAt) {
			throw new InvariantViolationException(
				"work-session-invalid-interval", "A session's finish instant must be after its start instant.");
		}

		var overlaps = _sessions.Values.Any(session =>
			session.Id != existing.Id
			&& session.LeafWorkId == existing.LeafWorkId
			&& session.WorkedByUserId == existing.WorkedByUserId
			&& RangesOverlap(request.StartedAt, request.FinishedAt, session.StartedAt, session.FinishedAt));
		if (overlaps) {
			throw new InvariantViolationException(
				"work-session-overlap", "This correction would overlap another session for the same worker and leaf.");
		}

		var updated = existing with {
			StartedAt = request.StartedAt,
			FinishedAt = request.FinishedAt,
			ChangedAt = NowToReturn,
			Version = existing.Version + 1,
		};
		_sessions[updated.Id] = updated;

		return Task.FromResult(updated);
	}

	public async Task<CompleteLeafResult> CompleteLeafAsync(CompleteLeafRequest request, CancellationToken cancellationToken = default)
	{
		var leafWork = nodePort.FindLeafWork(request.JobNodeId)
					   ?? throw new EntityNotFoundException($"Job node {request.JobNodeId} has no LeafWork attached.");

		var ownsNodeOrAncestor = nodePort.OwnsNodeOrAncestor(request.Context.Actor, request.JobNodeId);
		if (!AchievementAccessPolicy.CanSetAchievement(nodePort.RolesOf(request.Context.Actor), ownsNodeOrAncestor, false)) {
			throw new AuthorizationDeniedException($"Actor {request.Context.Actor} may not complete job node {request.JobNodeId}.");
		}

		if (leafWork.Version != request.Version) {
			throw new ConcurrencyConflictException(
				$"Expected version {request.Version} but the current version is {leafWork.Version}.");
		}

		if (!AchievementTransitions.IsPermitted(leafWork.Achievement, Achievement.Success)) {
			throw new InvariantViolationException(
				"achievement-transition-not-permitted", $"Cannot transition from {leafWork.Achievement} to {Achievement.Success}.");
		}

		var actualActive = _sessions.Values
			.Where(session => session.LeafWorkId == request.JobNodeId && session.FinishedAt is null)
			.OrderBy(session => session.Id.Value)
			.ToList();
		var expected = request.ExpectedActiveSessions
			.OrderBy(expectedSession => expectedSession.Id.Value)
			.ToList();
		var matchesExpected = actualActive.Count == expected.Count
							  && actualActive.Zip(expected)
								  .All(pair => pair.First.Id == pair.Second.Id && pair.First.Version == pair.Second.Version);
		if (!matchesExpected) {
			throw new ConcurrencyConflictException(
				"The leaf's current active-session set no longer matches the confirmed set.");
		}

		var finishedAt = request.FinishedAt ?? NowToReturn;
		if (request.FinishedAt is not null) {
			if (actualActive.Any(session => finishedAt <= session.StartedAt)) {
				throw new InvariantViolationException(
					"work-session-invalid-interval", "A session's finish instant must be after its start instant.");
			}

			if (finishedAt > NowToReturn) {
				throw new InvariantViolationException(
					"work-session-finish-in-future", "A session's finish instant must not be in the future.");
			}
		}

		var readinessInputs = await nodePort.GetReadinessInputsAsync(request.JobNodeId, cancellationToken).ConfigureAwait(false);
		var readiness = ReadinessCalculator.IsReady(request.JobNodeId, readinessInputs.NodesById, readinessInputs.Prerequisites);
		if (!readiness.IsReady) {
			throw new PrerequisiteBlockedException($"Job node {request.JobNodeId}'s prerequisites are not satisfied.");
		}

		var finished = new List<WorkSessionResult>();
		foreach (var session in actualActive) {
			var updated = session with { FinishedAt = finishedAt, ChangedAt = NowToReturn, Version = session.Version + 1 };
			_sessions[updated.Id] = updated;
			finished.Add(updated);
		}

		var updatedLeafWork = leafWork with { Achievement = Achievement.Success, ChangedAt = NowToReturn, Version = leafWork.Version + 1 };
		nodePort.SetLeafWork(updatedLeafWork);

		return new() {
			JobNodeId = request.JobNodeId,
			Achievement = Achievement.Success,
			ChangedAt = updatedLeafWork.ChangedAt,
			Version = updatedLeafWork.Version,
			FinishedSessions = [.. finished],
		};
	}

	public async Task<ReopenAndStartWorkResult> ReopenAndStartWorkAsync(
		ReopenAndStartWorkRequest request, CancellationToken cancellationToken = default)
	{
		var leafWork = nodePort.FindLeafWork(request.JobNodeId)
					   ?? throw new EntityNotFoundException($"Job node {request.JobNodeId} has no LeafWork attached.");

		if (leafWork.Version != request.Version) {
			throw new ConcurrencyConflictException(
				$"Expected version {request.Version} but the current version is {leafWork.Version}.");
		}

		if (!AchievementTransitions.IsPermitted(leafWork.Achievement, Achievement.Waiting)) {
			throw new InvariantViolationException(
				"achievement-transition-not-permitted", $"Cannot reopen from {leafWork.Achievement}.");
		}

		var node = nodePort.FindNode(request.JobNodeId);
		if (node?.ArchivedAt is not null) {
			throw new InvariantViolationException(
				"work-session-leaf-closed", "An archived node's leaf must be restored before it can be reopened.");
		}

		var actorControlsNode = nodePort.OwnsNodeOrAncestor(request.Context.Actor, request.JobNodeId);
		var actorParticipatedPreviously = _sessions.Values.Any(session =>
			session.LeafWorkId == request.JobNodeId && session.WorkedByUserId == request.Context.Actor);
		if (!LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
				nodePort.RolesOf(request.Context.Actor), actorControlsNode, actorParticipatedPreviously,
				request.Context.Actor, request.WorkedByUserId)) {
			throw new AuthorizationDeniedException(
				$"Actor {request.Context.Actor} may not reopen and start job node {request.JobNodeId} for {request.WorkedByUserId}.");
		}

		var startedAt = request.StartedAt ?? NowToReturn;
		if (startedAt > NowToReturn) {
			throw new InvariantViolationException(
				"work-session-start-in-future", "A session's start instant must not be in the future.");
		}

		if (_sessions.Values.Any(session =>
				session.LeafWorkId == request.JobNodeId
				&& session.WorkedByUserId == request.WorkedByUserId
				&& RangesOverlap(startedAt, null, session.StartedAt, session.FinishedAt))) {
			throw new InvariantViolationException(
				"work-session-overlap", "This session would overlap another session for the same worker and leaf.");
		}

		var updatedLeafWork = leafWork with { Achievement = Achievement.InProgress, ChangedAt = NowToReturn, Version = leafWork.Version + 1 };
		nodePort.SetLeafWork(updatedLeafWork);

		var session = new WorkSessionResult {
			Id = new(_nextId++),
			LeafWorkId = request.JobNodeId,
			WorkedByUserId = request.WorkedByUserId,
			StartedAt = startedAt,
			ChangedAt = NowToReturn,
			Version = 1,
		};
		_sessions[session.Id] = session;

		return new() {
			JobNodeId = request.JobNodeId,
			Achievement = Achievement.InProgress,
			ChangedAt = updatedLeafWork.ChangedAt,
			Version = updatedLeafWork.Version,
			Session = session,
		};
	}

	private static bool RangesOverlap(Instant aStart, Instant? aFinish, Instant bStart, Instant? bFinish)
	{
		var aEnd = aFinish ?? Instant.MaxValue;
		var bEnd = bFinish ?? Instant.MaxValue;

		return aStart < bEnd && bStart < aEnd;
	}

	private void AuthorizeOrThrow(AppUserId actorId, JobNodeId leafId)
	{
		if (!WorkSessionAccessPolicy.CanManage(nodePort.RolesOf(actorId), nodePort.OwnsNodeOrAncestor(actorId, leafId))) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not manage a session on job node {leafId}.");
		}
	}

	/// <summary>ADR 0045 §5: the narrow self-finish exception <see cref="WorkSessionAccessPolicy.CanFinishSession" /> adds.</summary>
	private void AuthorizeFinishOrThrow(AppUserId actorId, JobNodeId leafId, AppUserId sessionWorkedByUserId)
	{
		if (!WorkSessionAccessPolicy.CanFinishSession(
				nodePort.RolesOf(actorId), nodePort.OwnsNodeOrAncestor(actorId, leafId), actorId == sessionWorkedByUserId)) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not finish this session on job node {leafId}.");
		}
	}

	private static void CheckVersionOrThrow(long currentVersion, long expectedVersion)
	{
		if (currentVersion != expectedVersion) {
			throw new ConcurrencyConflictException(
				$"Expected version {expectedVersion} but the current version is {currentVersion}.");
		}
	}

	private WorkSessionResult GetExisting(WorkSessionId id) =>
		_sessions.TryGetValue(id, out var session) ? session : throw new EntityNotFoundException($"Work session {id} does not exist.");
}
