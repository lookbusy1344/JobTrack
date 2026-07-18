namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     The persistence-owned port backing <see cref="IAuditQueries.SearchAuditEventsAsync" /> (plan
///     §7.3 step 11). Materializes every matching raw event, unconditionally of the actor's
///     permissions — <see cref="AuditQueries" /> applies <see cref="Domain.Authorization.AuditAccessPolicy" />
///     and the per-event sensitive-field projection itself, the same reload-then-decide shape as the
///     other read-only ports.
/// </summary>
public interface IAuditQueryPort
{
	/// <summary>Loads the actor's current roles for an authorization pre-check before audit search materialization.</summary>
	/// <exception cref="EntityNotFoundException">The actor does not exist.</exception>
	Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		AppUserId actorId, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IAuditQueries.SearchAuditEventsAsync" />
	Task<AuditSearchQueryResult> SearchAuditEventsAsync(
		AppUserId actorId, AuditEventSearchFilter filter, CancellationToken cancellationToken = default);
}
