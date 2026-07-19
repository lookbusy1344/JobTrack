namespace JobTrack.Application.Tests;

using Abstractions;
using Ports;

/// <summary>
///     An in-memory fake of <see cref="IWorkSessionQueryPort" /> for application-slice tests (plan §7.3:
///     "write application tests with fake ports, then provider conformance tests using real
///     databases").
/// </summary>
internal sealed class FakeWorkSessionQueryPort : IWorkSessionQueryPort
{
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

		return Task.FromResult(new WorkSessionQueryResult { ActorRoles = actorRoles, Sessions = [.. sessions] });
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

		return Task.FromResult(new WorkSessionQueryResult { ActorRoles = actorRoles, Sessions = [.. sessions] });
	}

	public void SeedRoles(AppUserId actorId, params EmployeeRole[] roles) => _roles[actorId] = [.. roles];

	public void SeedLeaf(JobNodeId leafWorkId) => _leaves.Add(leafWorkId);

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
