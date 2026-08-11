namespace JobTrack.Persistence.Shared.Entities;

using Abstractions;
using NodaTime;

/// <summary>
///     Persistence shape of the append-only <c>audit_event</c> table (schema version 0012).
///     <see cref="EntityId" /> is deliberately not a strongly typed identifier or foreign key -- one row
///     may describe a change to any of several different entity tables. <see cref="BeforeData" />/
///     <see cref="AfterData" /> are the raw JSON text a provider-specific column type wraps (PostgreSQL
///     <c>jsonb</c>, SQLite <c>TEXT</c>); the query port parses them, this entity does not.
/// </summary>
internal sealed class AuditEventEntity
{
	public required AuditEventId Id { get; set; }

	public Instant OccurredAt { get; set; }

	/// <summary>
	///     Null for an unknown-subject authentication failure -- no <c>app_user</c> matched the attempted
	///     username, so there is no real actor to attribute the event to (fresh-eyes review §2.6).
	/// </summary>
	public AppUserId? ActorUserId { get; set; }

	public required string Operation { get; set; }

	public required string EntityType { get; set; }

	public long EntityId { get; set; }

	public Guid CorrelationId { get; set; }

	public string? Reason { get; set; }

	public string? BeforeData { get; set; }

	public string? AfterData { get; set; }
}
