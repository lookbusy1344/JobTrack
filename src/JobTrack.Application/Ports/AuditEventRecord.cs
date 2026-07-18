namespace JobTrack.Application.Ports;

using Abstractions;
using NodaTime;

/// <summary>
///     One raw <c>audit_event</c> row (spec §16), before <see cref="AuditQueries" /> applies its
///     per-event sensitive-field projection. <see cref="IsSensitive" /> is a persistence-determined fact
///     — whether <see cref="EntityType" /> names a rate/cost-bearing entity (<c>user_cost_rate</c>,
///     <c>node_rate_override</c>) — not an authorization decision;
///     <see
///         cref="Domain.Authorization.CostAccessPolicy" />
///     decides what a specific caller may see of it.
/// </summary>
public sealed record AuditEventRecord
{
	/// <summary>The audit event's identifier.</summary>
	public required AuditEventId Id { get; init; }

	/// <summary>The instant this event was recorded.</summary>
	public required Instant OccurredAt { get; init; }

	/// <summary>The user who performed the audited operation.</summary>
	public required AppUserId ActorId { get; init; }

	/// <summary>The operation performed (e.g. <c>"add-user-cost-rate"</c>).</summary>
	public required string Operation { get; init; }

	/// <summary>The affected entity's table/kind (e.g. <c>"job_node"</c>, <c>"user_cost_rate"</c>).</summary>
	public required string EntityType { get; init; }

	/// <summary>The affected entity's identifier. Not a strongly typed ID — one <c>audit_event</c> row may describe any entity table.</summary>
	public required long EntityId { get; init; }

	/// <summary>Correlates this event with the other events and logs the same operation produced.</summary>
	public required Guid CorrelationId { get; init; }

	/// <summary>Why this change was made, if the operation requires a reason.</summary>
	public required string? Reason { get; init; }

	/// <summary>The entity's structured field values before the change, or <see langword="null" /> on creation.</summary>
	public required EquatableDictionary<string, string?>? BeforeData { get; init; }

	/// <summary>The entity's structured field values after the change, or <see langword="null" /> on deletion.</summary>
	public required EquatableDictionary<string, string?>? AfterData { get; init; }

	/// <summary>Whether <see cref="BeforeData" />/<see cref="AfterData" /> carry rate/cost-bearing fields.</summary>
	public required bool IsSensitive { get; init; }
}
