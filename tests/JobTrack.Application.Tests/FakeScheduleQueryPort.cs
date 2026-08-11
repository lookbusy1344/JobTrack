namespace JobTrack.Application.Tests;

using Abstractions;
using Ports;

/// <summary>
///     An in-memory fake of <see cref="IScheduleQueryPort" /> for application-slice tests (plan §7.3:
///     "write application tests with fake ports, then provider conformance tests using real
///     databases").
/// </summary>
internal sealed class FakeScheduleQueryPort : IScheduleQueryPort
{
	private readonly Dictionary<AppUserId, List<ScheduleExceptionResult>> _exceptions = [];
	private readonly Dictionary<AppUserId, EquatableArray<EmployeeRole>> _roles = [];
	private readonly Dictionary<AppUserId, List<ScheduleVersionResult>> _versions = [];

	public Task<ScheduleQueryResult> GetScheduleAsync(
		AppUserId actorId, AppUserId userId, CancellationToken cancellationToken = default)
	{
		if (!_roles.TryGetValue(actorId, out var actorRoles)) {
			throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		}

		if (!_versions.TryGetValue(userId, out var versions)) {
			throw new EntityNotFoundException($"Employee {userId} does not exist.");
		}

		return Task.FromResult(new ScheduleQueryResult { ActorRoles = actorRoles, Versions = [.. versions], Exceptions = [.. _exceptions[userId]] });
	}

	public void SeedRoles(AppUserId actorId, params EmployeeRole[] roles) => _roles[actorId] = [.. roles];

	public void SeedEmployee(AppUserId userId)
	{
		_versions.TryAdd(userId, []);
		_exceptions.TryAdd(userId, []);
	}

	public void SeedVersion(ScheduleVersionResult version)
	{
		SeedEmployee(version.UserId);
		_versions[version.UserId].Add(version);
	}

	public void SeedException(ScheduleExceptionResult exception)
	{
		SeedEmployee(exception.UserId);
		_exceptions[exception.UserId].Add(exception);
	}
}
