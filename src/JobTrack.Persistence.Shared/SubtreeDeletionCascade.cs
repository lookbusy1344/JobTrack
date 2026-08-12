namespace JobTrack.Persistence.Shared;

using Abstractions;
using Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
///     Removes a whole subtree and every row that depends on it (ADR 0061), shared by both providers so
///     the cascade order is defined once. Every foreign key into <c>job_node</c> is
///     <c>ON DELETE RESTRICT</c> except <c>employee.home_node_id</c>, which is <c>ON DELETE SET NULL</c>
///     (schema version 0004) and clears itself — so every other dependent must be removed here, in
///     dependency order, or the delete fails.
/// </summary>
/// <remarks>
///     The caller owns the transaction: this only stages/issues the deletes, and
///     <c>SaveChangesAndCommitForDeleteAsync</c> commits them together with the audit event. Nothing
///     here is valid outside an explicit transaction, since a partially applied cascade would leave
///     orphaned work history.
/// </remarks>
internal static class SubtreeDeletionCascade
{
	/// <summary>
	///     Deletes, in dependency order, every <c>work_session</c>, <c>leaf_work</c>,
	///     <c>node_rate_override</c>, <c>job_request</c> (taking its <c>job_request_note</c> thread with
	///     it by <c>ON DELETE CASCADE</c>, ADR 0068), and <c>job_prerequisite</c> row belonging to the
	///     subtree, then every <c>job_node</c> row <em>below</em> the root, deepest-first so no parent is
	///     removed before its children.
	/// </summary>
	/// <remarks>
	///     The subtree root's own <c>job_node</c> row is deliberately left for the caller to remove
	///     through a tracked entity, so EF's <c>row_version</c> concurrency token is enforced on it.
	///     Everything here runs as set-based <c>ExecuteDelete</c>, which does not check that token: were
	///     the root deleted the same way, two administrators concurrently deleting one subtree would
	///     both report success (the loser silently deleting zero rows) and both write a
	///     <c>delete-subtree</c> audit event for a single deletion.
	/// </remarks>
	/// <param name="context">The open, transacted context the cascade issues its deletes through.</param>
	/// <param name="impact">The subtree manifest computed inside the same transaction the deletes run in.</param>
	/// <param name="deleteWorkSessionsForLeafWorkAsync">
	///     Deletes every <c>work_session</c> row for the given <c>leaf_work_id</c>s. Injected rather than
	///     issued directly here because the two providers reach it differently: SQLite has no roles, so
	///     a plain <c>ExecuteDelete</c> is correct there, while PostgreSQL's <c>jobtrack_domain</c> role
	///     has no direct DELETE grant on <c>work_session</c> at all (ADR 0036/0061 are the two accepted
	///     exceptions to "cost-relevant history is never deleted") and must go through the narrow
	///     <c>force_delete_work_sessions</c> SECURITY DEFINER function instead.
	/// </param>
	/// <param name="cancellationToken">Cancellation for the deletes issued here.</param>
	/// <returns>How many prerequisite edges were dropped, both internal and subtree-crossing.</returns>
	public static async Task<int> ExecuteAsync(
		DbContext context,
		SubtreeImpactData impact,
		Func<DbContext, IReadOnlyList<JobNodeId>, CancellationToken, Task> deleteWorkSessionsForLeafWorkAsync,
		CancellationToken cancellationToken)
	{
		var nodeIds = impact.Nodes.Select(n => n.Id).ToList();
		var leafWorkIds = impact.Nodes.Where(n => n.HasLeafWork).Select(n => n.Id).ToList();

		if (leafWorkIds.Count > 0) {
			await deleteWorkSessionsForLeafWorkAsync(context, leafWorkIds, cancellationToken).ConfigureAwait(false);
			_ = await context.Set<LeafWorkEntity>()
							 .Where(lw => leafWorkIds.Contains(lw.JobNodeId))
							 .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
		}

		_ = await context.Set<NodeRateOverrideEntity>()
						 .Where(o => nodeIds.Contains(o.NodeId))
						 .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

		// job_request_note is deliberately not deleted here: ADR 0034's append-only trigger refuses a
		// note deletion while its request still exists, so deleting the thread directly aborted the
		// whole cascade and made any subtree holding a commented request permanently undeletable.
		// ADR 0068 makes the note foreign key ON DELETE CASCADE instead, so the thread goes with the
		// job_request row below and append-only still holds everywhere else.
		_ = await context.Set<JobRequestEntity>()
						 .Where(r => nodeIds.Contains(r.JobNodeId))
						 .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

		// ADR 0061 drops every edge touching the subtree, including one arriving from a node outside
		// it -- reversing ADR 0036's refusal. An external dependent therefore loses a prerequisite and
		// may become ready; that is a valid state per ADR 0051, and the manifest named it beforehand.
		var edgesDropped = await context.Set<JobPrerequisiteEntity>()
										.Where(e => nodeIds.Contains(e.FromId) || nodeIds.Contains(e.ToId))
										.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

		// job_node.parent_id is RESTRICT, so a parent may not go before its children. Deleting one
		// depth level at a time, deepest first, makes that ordering explicit rather than relying on
		// EF's topological sort over a self-referencing relationship. Depth 0 (the root) is excluded
		// -- see the remarks above.
		foreach (var depthGroup in impact.Nodes.Where(n => n.Depth > 0).GroupBy(n => n.Depth).OrderByDescending(g => g.Key)) {
			var idsAtDepth = depthGroup.Select(n => n.Id).ToList();
			_ = await context.Set<JobNodeEntity>()
							 .Where(n => idsAtDepth.Contains(n.Id))
							 .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
		}

		return edgesDropped;
	}
}
