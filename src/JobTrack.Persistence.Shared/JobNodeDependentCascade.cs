namespace JobTrack.Persistence.Shared;

using System.Globalization;
using Abstractions;
using Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
///     Clears the dependent rows a <em>single-node</em> deletion has to remove before its
///     <c>job_node</c> row can go (ADR 0068), shared by both providers so the two ports cannot drift.
///     Every foreign key into <c>job_node</c> is <c>ON DELETE RESTRICT</c> bar
///     <c>app_user.home_node_id</c>, so anything this misses fails at the database as a raw constraint
///     violation and reaches the caller as the catch-all "has dependent data" — which is exactly how
///     <c>job_request</c> and <c>node_rate_override</c> made request-intake leaves undeletable before
///     this existed. <see cref="SubtreeDeletionCascade" /> is the recursive counterpart; the two are
///     deliberately kept in step, and <c>JobNodeDependentTableCoverageTests</c> fails if a new
///     dependent table is added to the schema without being handled in both.
/// </summary>
internal static class JobNodeDependentCascade
{
	/// <summary>
	///     Removes the node's <c>node_rate_override</c> and <c>job_request</c> rows (the request's
	///     <c>job_request_note</c> thread follows by <c>ON DELETE CASCADE</c>, ADR 0068), and refuses
	///     the deletion outright when a <c>request_holding_area</c> is anchored at the node — an area is
	///     configuration outliving the node, so it is re-anchored deliberately rather than destroyed
	///     silently, matching the refusal <see cref="SubtreeImpactComputation" /> already produces for a
	///     subtree.
	/// </summary>
	/// <returns>Audit-snapshot fields describing what was destroyed, empty when nothing depended on the node.</returns>
	/// <exception cref="InvariantViolationException">A request holding area is anchored at the node.</exception>
	public static async Task<Dictionary<string, string?>> RemoveDependentsOfAsync(
		DbContext context, JobNodeId nodeId, CancellationToken cancellationToken)
	{
		var anchoredAreas = await context.Set<RequestHoldingAreaEntity>().AsNoTracking()
			.Where(h => h.JobNodeId == nodeId)
			.Select(h => h.Name)
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		if (anchoredAreas.Count > 0) {
			throw new InvariantViolationException(
				"job-node-holding-area-anchored",
				"A request holding area is anchored at this job node; re-anchor or deactivate it first: " +
				string.Join(", ", anchoredAreas));
		}

		var noteCount = await context.Set<JobRequestNoteEntity>().AsNoTracking()
			.CountAsync(n => n.JobNodeId == nodeId, cancellationToken).ConfigureAwait(false);

		var rateOverrideCount = await context.Set<NodeRateOverrideEntity>()
			.Where(o => o.NodeId == nodeId)
			.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

		// The note thread goes with it at the database, so nothing here deletes job_request_note
		// directly -- ADR 0034's append-only trigger still refuses that while the request exists.
		var requestCount = await context.Set<JobRequestEntity>()
			.Where(r => r.JobNodeId == nodeId)
			.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

		var snapshot = new Dictionary<string, string?>();

		if (requestCount > 0) {
			snapshot["destroyed_job_request"] = nodeId.Value.ToString(CultureInfo.InvariantCulture);
			snapshot["destroyed_job_request_note_count"] = noteCount.ToString(CultureInfo.InvariantCulture);
		}

		if (rateOverrideCount > 0) {
			snapshot["destroyed_node_rate_override_count"] = rateOverrideCount.ToString(CultureInfo.InvariantCulture);
		}

		return snapshot;
	}
}
