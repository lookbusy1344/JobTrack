namespace JobTrack.Application.Tests;

using Abstractions;
using NodaTime;
using Ports;

/// <summary>
///     An in-memory fake of <see cref="IAuditQueryPort" /> for application-slice tests. Applies the
///     search filter, keyset cursor, and limit unconditionally of the actor's permissions, mirroring
///     how a real persistence implementation would materialize matching rows before
///     <see cref="AuditQueries" /> decides what this specific caller may see of them.
/// </summary>
internal sealed class FakeAuditQueryPort : IAuditQueryPort
{
	private readonly List<AuditEventRecord> _events = [];
	private readonly Dictionary<AppUserId, EquatableArray<EmployeeRole>> _roles = [];

	public int GetActorRolesCallCount { get; private set; }

	public int SearchAuditEventsCallCount { get; private set; }

	public IReadOnlyList<int> ObservedLimits { get; private set; } = [];

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
		AuditEventSearchFilter filter, AuditEventSearchCursor? before, int limit, CancellationToken cancellationToken = default)
	{
		SearchAuditEventsCallCount++;
		ObservedLimits = [.. ObservedLimits, limit];

		var matches = _events.Where(record =>
				(filter.ActorId is not AppUserId actorFilter || record.ActorId == actorFilter)
				&& (filter.EntityType is null || record.EntityType == filter.EntityType)
				&& (filter.EntityId is not long entityIdFilter || record.EntityId == entityIdFilter)
				&& (filter.CorrelationId is not Guid correlationFilter || record.CorrelationId == correlationFilter)
				&& (filter.From is not Instant from || record.OccurredAt >= from)
				&& (filter.To is not Instant to || record.OccurredAt < to)
				&& (before is null
					|| record.OccurredAt < before.OccurredAt
					|| (record.OccurredAt == before.OccurredAt && record.Id < before.Id)))
			.OrderByDescending(record => record.OccurredAt)
			.ThenByDescending(record => record.Id)
			.Take(limit)
			.ToArray();

		return Task.FromResult(new AuditSearchQueryResult { Events = EquatableArray.CopyOf(matches) });
	}

	public void SeedRoles(AppUserId actorId, params EmployeeRole[] roles) => _roles[actorId] = [.. roles];

	public void SeedEvent(AuditEventRecord record) => _events.Add(record);
}
