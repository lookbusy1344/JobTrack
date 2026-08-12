namespace JobTrack.Application.Tests;

using Abstractions;
using Domain.Concurrency;
using Domain.Intervals;
using NodaTime;
using Ports;

/// <summary>
///     An in-memory fake of <see cref="IWorkSessionQueryPort" /> for application-slice tests (plan §7.3:
///     "write application tests with fake ports, then provider conformance tests using real
///     databases").
/// </summary>
internal sealed class FakeWorkSessionQueryPort : IWorkSessionQueryPort
{
	private readonly HashSet<(AppUserId ActorId, JobNodeId LeafWorkId)> _controlled = [];
	private readonly HashSet<JobNodeId> _leaves = [];
	private readonly Dictionary<AppUserId, EquatableArray<EmployeeRole>> _roles = [];
	private readonly Dictionary<(JobNodeId LeafWorkId, AppUserId WorkedByUserId), List<WorkSessionResult>> _sessions = [];

	public Task<WorkSessionQueryResult> GetSessionsAsync(
		AppUserId actorId, JobNodeId leafWorkId, AppUserId? workedByUserId,
		int offset = 0, int? limit = null, CancellationToken cancellationToken = default)
	{
		if (!_roles.TryGetValue(actorId, out var actorRoles)) {
			throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		}

		if (!_leaves.Contains(leafWorkId)) {
			throw new EntityNotFoundException($"Job node {leafWorkId} does not exist.");
		}

		// A null workedByUserId means "every worker's sessions on this leaf" (ADR 0041), so the fake
		// unions every keyed bucket for the leaf rather than looking up one worker's.
		IEnumerable<WorkSessionResult> matching;
		if (workedByUserId is AppUserId workerId) {
			matching = _sessions.TryGetValue((leafWorkId, workerId), out var found) ? found : [];
		} else {
			matching = _sessions.Where(entry => entry.Key.Item1 == leafWorkId).SelectMany(entry => entry.Value);
		}

		var ordered = matching
					  .OrderByDescending(s => s.StartedAt).ThenByDescending(s => s.Id.Value)
					  .Skip(offset);
		var sessions = limit.HasValue ? ordered.Take(limit.Value) : ordered;

		return Task.FromResult(new WorkSessionQueryResult {
			ActorRoles = actorRoles,
			Sessions = [.. sessions],
		});
	}

	public Task<WorkSessionQueryResult> GetActiveSessionsAsync(
		AppUserId actorId, EquatableArray<JobNodeId> leafWorkIds, CancellationToken cancellationToken = default)
	{
		if (!_roles.TryGetValue(actorId, out var actorRoles)) {
			throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		}

		var leafWorkIdSet = leafWorkIds.ToHashSet();
		var sessions = _sessions
					   .Where(kvp => leafWorkIdSet.Contains(kvp.Key.LeafWorkId))
					   .SelectMany(kvp => kvp.Value)
					   .Where(s => s.FinishedAt is null)
					   .ToArray();

		return Task.FromResult(new WorkSessionQueryResult {
			ActorRoles = actorRoles,
			Sessions = [.. sessions],
		});
	}

	public Task<WorkSessionManageCapabilityQueryResult> GetManageCapabilitiesAsync(
		AppUserId actorId, EquatableArray<JobNodeId> leafWorkIds, CancellationToken cancellationToken = default)
	{
		if (!_roles.TryGetValue(actorId, out var actorRoles)) {
			throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		}

		var controlled = leafWorkIds.Where(id => _controlled.Contains((actorId, id))).ToArray();

		return Task.FromResult(new WorkSessionManageCapabilityQueryResult {
			ActorRoles = actorRoles,
			ControlledLeafWorkIds = [.. controlled],
		});
	}

	public Task<ConcurrentWorkQueryResult> GetConcurrentSessionsAsync(
		JobNodeId nodeId, Instant asOf, int maxSubjectSessionCount, int maxConcurrentSessionCount,
		CancellationToken cancellationToken = default)
	{
		var all = _sessions.SelectMany(entry => entry.Value).ToList();
		var subject = all.Where(session => session.LeafWorkId == nodeId)
						 .OrderByDescending(session => session.StartedAt)
						 .Take(maxSubjectSessionCount)
						 .Select(session => ToConcurrentSession(session, asOf))
						 .ToList();
		var subjectWorkers = subject.Select(session => session.WorkedByUserId).ToHashSet();

		var concurrent = all.Where(session => session.LeafWorkId != nodeId && subjectWorkers.Contains(session.WorkedByUserId))
							.Select(session => ToConcurrentSession(session, asOf))
							.Where(candidate => subject.Any(s => s.WorkedByUserId == candidate.WorkedByUserId
																 && IntervalAlgebra.Overlaps(s.Interval, candidate.Interval)))
							.OrderByDescending(session => session.Interval.Start)
							.Take(maxConcurrentSessionCount)
							.ToList();

		return Task.FromResult(new ConcurrentWorkQueryResult {
			SubjectSessions = [.. subject],
			ConcurrentSessions = [.. concurrent],
			IsTruncated = subject.Count == maxSubjectSessionCount || concurrent.Count == maxConcurrentSessionCount,
		});
	}

	/// <summary>
	///     Clips an unfinished session at <paramref name="asOf" />, exactly as the real ports do. A
	///     session that started at or after <paramref name="asOf" /> has no finite interval to report and
	///     is therefore dropped by the callers above.
	/// </summary>
	private static ConcurrentWorkSession ToConcurrentSession(WorkSessionResult session, Instant asOf) =>
		new(session.Id, session.LeafWorkId, session.WorkedByUserId,
			new(session.StartedAt, session.FinishedAt ?? asOf));

	public void SeedRoles(AppUserId actorId, params EmployeeRole[] roles) => _roles[actorId] = [.. roles];

	public void SeedLeaf(JobNodeId leafWorkId) => _leaves.Add(leafWorkId);

	public void SeedControl(AppUserId actorId, JobNodeId leafWorkId) => _controlled.Add((actorId, leafWorkId));

	public void SeedSession(WorkSessionResult session)
	{
		_leaves.Add(session.LeafWorkId);
		var key = (session.LeafWorkId, session.WorkedByUserId);
		if (!_sessions.TryGetValue(key, out var sessions)) {
			sessions = [];
			_sessions[key] = sessions;
		}

		sessions.Add(session);
	}
}
