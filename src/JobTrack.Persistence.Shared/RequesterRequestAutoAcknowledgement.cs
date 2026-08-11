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
	///     <para>
	///         Deliberately <em>not</em> a tracked read/check/mutate: two transactions starting work on
	///         different leaves under one unacknowledged request would both read the old
	///         <see cref="JobRequestEntity.RowVersion" />, and the loser's <c>SaveChangesAsync</c> would
	///         raise <see cref="DbUpdateConcurrencyException" /> and roll back a leaf command that never
	///         actually conflicted. The single conditional <c>UPDATE ... WHERE acknowledged_at IS NULL</c>
	///         below makes the race a genuine no-op instead: on both providers the loser's statement
	///         blocks on the winner's row lock, re-evaluates the predicate after that commit, and matches
	///         zero rows -- so the audit event is queued only by whichever transaction actually performed
	///         the acknowledgement, and never twice.
	///     </para>
	/// </summary>
	public static async Task AcknowledgeIfNeededAsync(
		DbContext context, JobNodeId leafNodeId, AppUserId actorId, Instant now, Guid correlationId, CancellationToken cancellationToken)
	{
		var anchorId = await JobNodeHierarchyQueries.GetNearestRequestAnchorIdAsync(context, leafNodeId.Value, cancellationToken)
			.ConfigureAwait(false);

		if (anchorId is not long anchorNodeId) {
			return;
		}

		var anchor = new JobNodeId(anchorNodeId);
		var acknowledged = await context.Set<JobRequestEntity>()
			.Where(r => r.JobNodeId == anchor && r.AcknowledgedAt == null)
			.ExecuteUpdateAsync(
				setters => setters
					.SetProperty(r => r.AcknowledgedAt, now)
					.SetProperty(r => r.AcknowledgedByUserId, actorId)
					.SetProperty(r => r.RowVersion, r => r.RowVersion + 1),
				cancellationToken)
			.ConfigureAwait(false);

		if (acknowledged == 0) {
			return;
		}

		AuditEventWriter.Add(
			context, actorId, now, Operation, "job_request", anchorNodeId, correlationId, null, null,
			new Dictionary<string, string?> { ["acknowledged_by_user_id"] = actorId.Value.ToString(CultureInfo.InvariantCulture) });
	}
}
