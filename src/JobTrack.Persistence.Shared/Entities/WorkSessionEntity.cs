namespace JobTrack.Persistence.Shared.Entities;

using Abstractions;
using NodaTime;

/// <summary>
///     Persistence shape of the <c>work_session</c> table (schema version 0007). The generated
///     <c>session_range</c> column is deliberately unmapped: no command or query in this slice reads
///     or writes it, and it is derived entirely from <see cref="StartedAt" />/<see cref="FinishedAt" />.
/// </summary>
internal sealed class WorkSessionEntity
{
	public required WorkSessionId Id { get; set; }

	public required JobNodeId LeafWorkId { get; set; }

	public required AppUserId WorkedByUserId { get; set; }

	public Instant StartedAt { get; set; }

	public Instant? FinishedAt { get; set; }

	public Instant ChangedAt { get; set; }

	public long RowVersion { get; set; }
}
