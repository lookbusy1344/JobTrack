namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     Optional narrowing criteria for <see cref="IAuditQueries.SearchAuditEventsAsync" />. Every member
///     is optional; an absent filter matches every event. <see cref="EntityId" /> is a bare identifier,
///     not a strongly typed ID, because one <c>audit_event</c> row may describe any entity table (spec
///     §16) — there is no single referential target to type it against.
/// </summary>
public sealed record AuditEventSearchFilter
{
	/// <summary>Restricts results to events performed by this actor.</summary>
	public AppUserId? ActorId { get; init; }

	/// <summary>Restricts results to this entity table/kind (e.g. <c>"job_node"</c>).</summary>
	public string? EntityType { get; init; }

	/// <summary>Restricts results to this entity's identifier. Only meaningful together with <see cref="EntityType" />.</summary>
	public long? EntityId { get; init; }

	/// <summary>Restricts results to this operation's correlated events.</summary>
	public Guid? CorrelationId { get; init; }

	/// <summary>Restricts results to events at or after this instant.</summary>
	public Instant? From { get; init; }

	/// <summary>Restricts results to events strictly before this instant.</summary>
	public Instant? To { get; init; }
}
