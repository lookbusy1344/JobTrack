namespace JobTrack.Persistence.Shared;

using System.Text.Json;
using Abstractions;
using Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

/// <summary>
///     Adds one <c>audit_event</c> row to an already-open <see cref="DbContext" />'s change tracker
///     (spec §16, impl plan §7.3: "every command... emits audit intent, and commits once"), so the
///     event is written in the same transaction as the mutation it describes. Both providers' command
///     ports call this immediately before their own <c>SaveChangesAsync</c> -- it never opens a
///     connection, transaction, or context itself, matching every other shared helper under this
///     project's <c>Abstractions</c>-only scope (impl plan §7.4 project layout).
/// </summary>
public static class AuditEventWriter
{
	/// <summary>
	///     Queues an audit event for <paramref name="entityType" />/<paramref name="entityId" />.
	///     <paramref name="beforeData" /> is <see langword="null" /> on creation; <paramref name="afterData" />
	///     is <see langword="null" /> on deletion. Both are serialized as flat JSON objects (ADR 0003: "the
	///     full before and after row content"). <paramref name="actorId" /> is <see langword="null" /> only
	///     for an unknown-subject authentication failure (fresh-eyes review §2.6) -- every other caller
	///     passes a real actor.
	/// </summary>
	public static void Add(
		DbContext context,
		AppUserId? actorId,
		Instant occurredAt,
		string operation,
		string entityType,
		long entityId,
		Guid correlationId,
		string? reason,
		IReadOnlyDictionary<string, string?>? beforeData,
		IReadOnlyDictionary<string, string?>? afterData)
	{
		_ = context.Add(new AuditEventEntity {
			Id = default,
			OccurredAt = occurredAt,
			ActorUserId = actorId,
			Operation = operation,
			EntityType = entityType,
			EntityId = entityId,
			CorrelationId = correlationId,
			Reason = reason,
			BeforeData = beforeData is null ? null : JsonSerializer.Serialize(beforeData),
			AfterData = afterData is null ? null : JsonSerializer.Serialize(afterData),
		});
	}
}
