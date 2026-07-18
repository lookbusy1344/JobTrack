namespace JobTrack.Persistence.PostgreSql;

using Application;
using Microsoft.EntityFrameworkCore;
using Shared.Entities;

/// <summary>
///     Loads structural facts for a <see cref="JobNodeEntity" /> and projects a <see cref="JobNodeResult" />.
/// </summary>
internal static class JobNodeStructuralProjection
{
	public static JobNodeResult ToResult(JobNodeEntity node, bool hasChildren, bool hasLeafWork) =>
		JobNodeStructuralResults.ToResult(
			node.Id,
			node.ParentId,
			node.Description,
			node.WriteUp,
			node.PostedByUserId,
			node.OwnerUserId,
			node.ExpectedDurationHours,
			node.ExpectedCost,
			node.NeededStart,
			node.NeededFinish,
			node.Priority,
			node.PostedAt,
			node.ArchivedAt,
			node.RowVersion,
			hasChildren,
			hasLeafWork);

	public static async Task<JobNodeResult> ToResultAsync(
		DbContext context, JobNodeEntity node, CancellationToken cancellationToken)
	{
		var hasChildren = await context.Set<JobNodeEntity>().AsNoTracking()
			.AnyAsync(c => c.ParentId == node.Id, cancellationToken).ConfigureAwait(false);
		var hasLeafWork = await context.Set<LeafWorkEntity>().AsNoTracking()
			.AnyAsync(lw => lw.JobNodeId == node.Id, cancellationToken).ConfigureAwait(false);
		return ToResult(node, hasChildren, hasLeafWork);
	}
}
