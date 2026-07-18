namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     Result of <see cref="IAuditQueryPort.SearchAuditEventsAsync" />: the actor's current roles (so
///     <see cref="AuditQueries" /> can apply <see cref="Domain.Authorization.AuditAccessPolicy" /> and
///     the per-event sensitive-field projection without a second round-trip) alongside the matching raw
///     events.
/// </summary>
public sealed record AuditSearchQueryResult
{
	/// <summary>The acting user's currently assigned roles.</summary>
	public required EquatableArray<EmployeeRole> ActorRoles { get; init; }

	/// <summary>The matching audit events, most recent first.</summary>
	public required EquatableArray<AuditEventRecord> Events { get; init; }
}
