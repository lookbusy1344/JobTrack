namespace JobTrack.Persistence.Shared;

using Abstractions;
using Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

/// <summary>
///     Measures exactly what a recursive subtree deletion would destroy (ADR 0061), shared by both
///     providers so the manifest shown on the confirmation screen and the manifest recomputed inside
///     the deleting transaction can never diverge. Every count is exact over the whole subtree:
///     <see cref="JobNodeHierarchyQueries.GetSubtreeImpactRowsAsync" /> is unbounded, unlike the
///     depth/breadth-capped Browse subtree query, which would under-report what deletion destroys.
/// </summary>
internal static class SubtreeImpactComputation
{
	/// <summary>
	///     Gathers the subtree's structural rows and every dependent row that a deletion would have to
	///     remove. Session durations are summed here over the typed <see cref="WorkSessionEntity" /> set
	///     rather than in the recursive SQL, because only the typed set carries each provider's
	///     <see cref="Instant" /> value-converter configuration.
	/// </summary>
	/// <exception cref="EntityNotFoundException">The root node does not exist.</exception>
	public static async Task<SubtreeImpactData> ComputeAsync(
		DbContext context, JobNodeId rootId, CancellationToken cancellationToken)
	{
		var rows = await JobNodeHierarchyQueries.GetSubtreeImpactRowsAsync(context, rootId.Value, cancellationToken)
												.ConfigureAwait(false);
		if (rows.Count == 0) {
			throw new EntityNotFoundException($"Job node {rootId} does not exist.");
		}

		var nodeIds = rows.Select(r => new JobNodeId(r.Id)).ToList();
		var leafWorkIds = rows.Where(r => r.HasLeafWork).Select(r => new JobNodeId(r.Id)).ToList();

		var sessionImpact = await LoadSessionImpactAsync(context, leafWorkIds, cancellationToken).ConfigureAwait(false);

		var edges = await context.Set<JobPrerequisiteEntity>().AsNoTracking()
								 .Where(e => nodeIds.Contains(e.FromId) || nodeIds.Contains(e.ToId))
								 .Select(e => new
								 {
									 e.FromId,
									 e.ToId,
								 })
								 .ToListAsync(cancellationToken).ConfigureAwait(false);

		var inside = nodeIds.ToHashSet();
		var externalEdges = edges.Where(e => !inside.Contains(e.FromId) || !inside.Contains(e.ToId)).ToList();
		var externalNodeIds = externalEdges
							  .Select(e => inside.Contains(e.FromId) ? e.ToId : e.FromId)
							  .Distinct()
							  .ToList();

		var externalDescriptions = externalNodeIds.Count == 0
			? []
			: await context.Set<JobNodeEntity>().AsNoTracking()
						   .Where(n => externalNodeIds.Contains(n.Id))
						   .ToDictionaryAsync(n => n.Id, n => n.Description, cancellationToken).ConfigureAwait(false);

		var jobRequestCount = await context.Set<JobRequestEntity>().AsNoTracking()
										   .CountAsync(r => nodeIds.Contains(r.JobNodeId), cancellationToken).ConfigureAwait(false);

		var holdingAreas = await context.Set<RequestHoldingAreaEntity>().AsNoTracking()
										.Where(h => nodeIds.Contains(h.JobNodeId))
										.Select(h => new SubtreeImpactHoldingAreaData(h.Id, h.Name, h.JobNodeId))
										.ToListAsync(cancellationToken).ConfigureAwait(false);

		var root = rows.First(r => r.Id == rootId.Value);

		// The row set is the complete descendant closure, so "has a child here" is the same fact as
		// "has a child at all" -- which is what ADR 0035 derives Branch/Leaf from, rather than a
		// stored column.
		var parentIds = rows.Where(r => r.ParentId is not null).Select(r => r.ParentId!.Value).ToHashSet();

		NodeKind KindOf(SubtreeImpactRow row) => row.ParentId switch {
			null => NodeKind.Root,
			_ when parentIds.Contains(row.Id) => NodeKind.Branch,
			_ => NodeKind.Leaf,
		};

		var nodes = rows
					.OrderBy(r => r.Depth).ThenBy(r => r.Id)
					.Select(r => new SubtreeImpactNodeData(
						new(r.Id),
						r.ParentId is long parentId ? new JobNodeId(parentId) : null,
						r.Depth,
						r.Description,
						KindOf(r),
						r.HasLeafWork ? (Achievement)r.AchievementId!.Value : null,
						r.HasLeafWork,
						sessionImpact.CountByLeaf.GetValueOrDefault(new(r.Id)),
						r.IsArchived))
					.ToList();

		return new(
			rootId,
			EquatableArray.CopyOf(nodes),
			rows.Count,
			rows.Count(r => r.HasLeafWork),
			sessionImpact.SessionCount,
			sessionImpact.TotalWorked,
			edges.Count - externalEdges.Count,
			EquatableArray.CopyOf(externalEdges
								  .Select(e => new SubtreeImpactPrerequisiteEdgeData(
									  e.FromId,
									  e.ToId,
									  externalDescriptions.GetValueOrDefault(inside.Contains(e.FromId) ? e.ToId : e.FromId, string.Empty),
									  !inside.Contains(e.ToId)))
								  .ToArray()),
			jobRequestCount,
			EquatableArray.CopyOf(holdingAreas),
			root.ParentId is null);
	}

	/// <summary>
	///     Sums the completed-session durations and per-leaf session counts a deletion would destroy.
	///     An active session (no finish) has contributed no completed work yet, so it adds nothing to
	///     the total even though the row itself is still destroyed and counted.
	/// </summary>
	private static async Task<(int SessionCount, Duration TotalWorked, IReadOnlyDictionary<JobNodeId, int> CountByLeaf)>
		LoadSessionImpactAsync(DbContext context, List<JobNodeId> leafWorkIds, CancellationToken cancellationToken)
	{
		var sessions = leafWorkIds.Count == 0
			? []
			: await context.Set<WorkSessionEntity>().AsNoTracking()
						   .Where(s => leafWorkIds.Contains(s.LeafWorkId))
						   .Select(s => new
						   {
							   s.LeafWorkId,
							   s.StartedAt,
							   s.FinishedAt,
						   })
						   .ToListAsync(cancellationToken).ConfigureAwait(false);

		var totalWorked = sessions.Aggregate(
			Duration.Zero,
			(running, s) => s.FinishedAt is Instant finishedAt ? running + (finishedAt - s.StartedAt) : running);

		var sessionCountByLeaf = sessions
								 .GroupBy(s => s.LeafWorkId)
								 .ToDictionary(g => g.Key, g => g.Count());

		return (sessions.Count, totalWorked, sessionCountByLeaf);
	}
}

/// <summary>The provider-neutral manifest produced by <see cref="SubtreeImpactComputation.ComputeAsync" />.</summary>
internal sealed record SubtreeImpactData(
	JobNodeId RootId,
	EquatableArray<SubtreeImpactNodeData> Nodes,
	int NodeCount,
	int LeafWorkCount,
	int WorkSessionCount,
	Duration TotalWorkedDuration,
	int InternalPrerequisiteEdgeCount,
	EquatableArray<SubtreeImpactPrerequisiteEdgeData> ExternalPrerequisiteEdges,
	int JobRequestCount,
	EquatableArray<SubtreeImpactHoldingAreaData> BlockingHoldingAreas,
	bool IsPermanentRoot);

/// <summary>One node inside a <see cref="SubtreeImpactData" />.</summary>
internal sealed record SubtreeImpactNodeData(
	JobNodeId Id,
	JobNodeId? ParentId,
	int Depth,
	string Description,
	NodeKind Kind,
	Achievement? Achievement,
	bool HasLeafWork,
	int WorkSessionCount,
	bool IsArchived);

/// <summary>One subtree-crossing prerequisite edge inside a <see cref="SubtreeImpactData" />.</summary>
internal sealed record SubtreeImpactPrerequisiteEdgeData(
	JobNodeId FromId,
	JobNodeId ToId,
	string ExternalDescription,
	bool ExternalNodeIsDependent);

/// <summary>One request holding area anchored inside a <see cref="SubtreeImpactData" />'s subtree.</summary>
internal sealed record SubtreeImpactHoldingAreaData(RequestHoldingAreaId Id, string Name, JobNodeId JobNodeId);
