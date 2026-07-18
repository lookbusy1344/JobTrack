namespace JobTrack.Persistence.Sqlite;

using Abstractions;
using Domain.Hierarchy;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.Entities;

/// <summary>
///     The in-transaction prerequisite recheck (impl plan §7.3 slice 6; spec §6: "the start... command
///     shall recheck prerequisites inside their write transaction"), shared by every SQLite command
///     that gates on readiness — <see cref="SqliteWorkSessionCommandPort" />'s start paths,
///     <see cref="SqliteAchievementCommandPort" />'s transition into a completed state, and
///     <see cref="SqliteJobNodeCommandPort" />'s subtree import when the batch carries recorded work.
///     Loads hierarchy facts through the shared portable ancestor/subtree SQL primitives, then decides
///     with the pure <see cref="ReadinessCalculator" /> — the same division of labour as
///     <c>SqliteJobNodeCommandPort.ValidatePrerequisiteEdgeAsync</c>, and duplicated across the two
///     providers (not across the callers within one) for the same reason, matching this codebase's
///     established convention.
/// </summary>
internal static class LeafReadiness
{
	/// <summary>
	///     Whether every prerequisite attached to <paramref name="leafId" /> or to any of its ancestors
	///     is satisfied, evaluated against the state visible in <paramref name="context" />'s open
	///     transaction.
	/// </summary>
	public static async Task<bool> IsReadyAsync(
		SqliteJobTrackDbContext context, JobNodeId leafId, CancellationToken cancellationToken)
	{
		var ancestorChain = await JobNodeHierarchyQueries.GetAncestorChainAsync(context, leafId.Value, cancellationToken)
			.ConfigureAwait(false);
		var ancestorIds = ancestorChain.Select(a => new JobNodeId(a.Id)).ToArray();

		var edges = await context.Set<JobPrerequisiteEntity>().AsNoTracking()
			.Where(jp => ancestorIds.Contains(jp.ToId))
			.Select(jp => new PrerequisiteEdge(jp.FromId, jp.ToId))
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		var nodesById = new Dictionary<JobNodeId, HierarchyNode>();
		foreach (var ancestor in ancestorChain) {
			var id = new JobNodeId(ancestor.Id);
			var parentId = ancestor.ParentId is { } p ? new JobNodeId(p) : (JobNodeId?)null;
			nodesById[id] = new(id, parentId, [], null);
		}

		foreach (var requiredJobId in edges.Select(e => e.RequiredJobId).Distinct()) {
			if (nodesById.ContainsKey(requiredJobId)) {
				continue;
			}

			var subtree = await JobNodeHierarchyQueries.GetSubtreeAchievementsAsync(context, requiredJobId.Value, cancellationToken)
				.ConfigureAwait(false);
			var childIdsByParent = subtree
				.Where(row => row.ParentId is not null)
				.GroupBy(row => row.ParentId!.Value)
				.ToDictionary(group => group.Key, group => group.Select(row => new JobNodeId(row.Id)).ToArray());

			foreach (var row in subtree) {
				var id = new JobNodeId(row.Id);
				var parentId = row.ParentId is { } p ? new JobNodeId(p) : (JobNodeId?)null;
				var childIds = childIdsByParent.TryGetValue(row.Id, out var kids) ? kids : [];
				var achievement = row.AchievementId is { } a ? (Achievement)a : (Achievement?)null;
				nodesById[id] = new(id, parentId, [.. childIds], achievement);
			}
		}

		return ReadinessCalculator.IsReady(leafId, nodesById, edges).IsReady;
	}
}
