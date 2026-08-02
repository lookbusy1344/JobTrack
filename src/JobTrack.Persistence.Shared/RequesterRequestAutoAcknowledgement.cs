namespace JobTrack.Persistence.Shared;

using System.Globalization;
using Abstractions;
using Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

/// <summary>
///     ADR 0058: the first time a leaf under an unacknowledged <c>job_request</c>'s subtree either
///     advances into <see cref="Achievement.InProgress" /> or reaches a terminal achievement, the
///     request is auto-acknowledged as a side effect of that same write. Defined once here (rather
///     than duplicated per provider) and called from inside each command port's own already-open
///     <see cref="DbContext" />/transaction, so the acknowledgement is never a second commit -- the
///     "compound writes are single ACID transactions" house-style rule this would otherwise violate
///     if it lived in <c>JobTrack.Application</c> instead, where a port's transaction has already
///     committed by the time control returns.
/// </summary>
internal static class RequesterRequestAutoAcknowledgement
{
	private const string Operation = "auto-acknowledge-request";

	/// <summary>
	///     Walks <paramref name="leafNodeId" />'s ancestor chain (including itself) for the nearest
	///     <c>job_request</c> anchor; if one exists and is not yet acknowledged, sets
	///     <see cref="JobRequestEntity.AcknowledgedAt" />/<see cref="JobRequestEntity.AcknowledgedByUserId" />
	///     and queues an <see cref="AuditEventWriter" /> entry. A silent no-op when no request anchors
	///     this leaf, or when it is already acknowledged -- this is a side effect of the triggering
	///     write, not the requester of the action, so it never throws.
	/// </summary>
	public static async Task AcknowledgeIfNeededAsync(
		DbContext context, JobNodeId leafNodeId, AppUserId actorId, Instant now, Guid correlationId, CancellationToken cancellationToken)
	{
		var ancestorIds = await JobNodeHierarchyQueries.GetAncestorIdsAsync(context, leafNodeId.Value, cancellationToken)
			.ConfigureAwait(false);
		var ancestorNodeIds = ancestorIds.Select(id => new JobNodeId(id)).ToArray();

		var jobRequest = await context.Set<JobRequestEntity>()
			.FirstOrDefaultAsync(r => ancestorNodeIds.Contains(r.JobNodeId), cancellationToken).ConfigureAwait(false);

		if (jobRequest is null || jobRequest.AcknowledgedAt is not null) {
			return;
		}

		jobRequest.AcknowledgedAt = now;
		jobRequest.AcknowledgedByUserId = actorId;
		jobRequest.RowVersion += 1;

		AuditEventWriter.Add(
			context, actorId, now, Operation, "job_request", jobRequest.JobNodeId.Value, correlationId, null, null,
			new Dictionary<string, string?> { ["acknowledged_by_user_id"] = actorId.Value.ToString(CultureInfo.InvariantCulture) });
	}
}
