namespace JobTrack.Application.Tests;

using Abstractions;
using Ports;

/// <summary>
///     An in-memory fake of <see cref="IAuditQueryPort" /> for application-slice tests. Applies the
///     search filter unconditionally of the actor's permissions, mirroring how a real persistence
///     implementation would materialize matching rows before <see cref="AuditQueries" /> decides what
///     this specific caller may see of them.
/// </summary>
internal sealed class FakeAuditQueryPort : IAuditQueryPort
{
	private readonly List<AuditEventRecord> _events = [];
	private readonly Dictionary<AppUserId, EquatableArray<EmployeeRole>> _roles = [];

	public int GetActorRolesCallCount { get; private set; }

	public int SearchAuditEventsCallCount { get; private set; }

	public Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		AppUserId actorId, CancellationToken cancellationToken = default)
	{
		GetActorRolesCallCount++;
		if (!_roles.TryGetValue(actorId, out var actorRoles)) {
			throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		}

		return Task.FromResult(actorRoles);
	}

	public Task<AuditSearchQueryResult> SearchAuditEventsAsync(
		AppUserId actorId, AuditEventSearchFilter filter, CancellationToken cancellationToken = default)
	{
		SearchAuditEventsCallCount++;
		var roles = _roles.TryGetValue(actorId, out var actorRoles) ? actorRoles : [];

		var matches = _events.Where(record =>
				(filter.ActorId is not { } actorFilter || record.ActorId == actorFilter)
				&& (filter.EntityType is not { } entityTypeFilter || record.EntityType == entityTypeFilter)
				&& (filter.EntityId is not { } entityIdFilter || record.EntityId == entityIdFilter)
				&& (filter.CorrelationId is not { } correlationFilter || record.CorrelationId == correlationFilter)
				&& (filter.From is not { } from || record.OccurredAt >= from)
				&& (filter.To is not { } to || record.OccurredAt < to))
			.OrderByDescending(record => record.OccurredAt)
			.ToArray();

		return Task.FromResult(new AuditSearchQueryResult { ActorRoles = roles, Events = EquatableArray.CopyOf(matches) });
	}

	public void SeedRoles(AppUserId actorId, params EmployeeRole[] roles) => _roles[actorId] = [.. roles];

	public void SeedEvent(AuditEventRecord record) => _events.Add(record);
}
