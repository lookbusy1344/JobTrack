namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     One audit event as exposed to a specific caller (spec §16, plan §6.5's "permission-sensitive
///     projections"). Actor, timestamp, operation, entity, and correlation are always visible — viewing
///     job/work/schedule information is an unqualified baseline capability (spec §7.3).
///     <see
///         cref="BeforeData" />
///     /<see cref="AfterData" /> are withheld (<see langword="null" />, with
///     <see
///         cref="IsRedacted" />
///     <see langword="true" />) when the underlying event carries rate/cost-bearing
///     fields and the caller lacks <see cref="Domain.Authorization.CostAccessPolicy" /> visibility —
///     "viewing or changing costs and rates shall require explicit permissions" (spec §16) applies to
///     audit history exactly as it does to live queries.
/// </summary>
public sealed record AuditEventResult
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

	/// <summary>The affected entity's identifier.</summary>
	public required long EntityId { get; init; }

	/// <summary>Correlates this event with the other events and logs the same operation produced.</summary>
	public required Guid CorrelationId { get; init; }

	/// <summary>Why this change was made, if the operation requires a reason.</summary>
	public required string? Reason { get; init; }

	/// <summary>The entity's structured field values before the change, withheld if <see cref="IsRedacted" />.</summary>
	public required EquatableDictionary<string, string?>? BeforeData { get; init; }

	/// <summary>The entity's structured field values after the change, withheld if <see cref="IsRedacted" />.</summary>
	public required EquatableDictionary<string, string?>? AfterData { get; init; }

	/// <summary>Whether <see cref="BeforeData" />/<see cref="AfterData" /> were withheld from this caller.</summary>
	public required bool IsRedacted { get; init; }
}
