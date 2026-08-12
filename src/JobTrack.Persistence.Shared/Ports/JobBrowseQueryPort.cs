namespace JobTrack.Persistence.Shared.Ports;

using System.Data;
using Abstractions;
using Application;
using Application.Ports;
using Domain.Hierarchy;
using Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

/// <summary>
///     The provider-neutral body of <see cref="IJobBrowseQueryPort" /> (plan §8.5 slice 2). One
///     context per call, read-only throughout. Reuses
///     <see cref="JobNodeHierarchyQueries.GetAncestorChainAsync" /> for the ancestor walk (impl plan
///     §7.4's sanctioned recursive-SQL exception) rather than adding new raw SQL.
/// </summary>
/// <remarks>
///     Everything here is expressible in provider-agnostic LINQ/EF, so both providers share this one
///     body rather than keeping near-identical copies that must be patched in lockstep.
///     <see cref="IJobBrowseProviderOperations" /> is the complete, enumerable list of what actually
///     differs between them -- read it to know the whole provider-specific surface of this port.
/// </remarks>
internal sealed class JobBrowseQueryPort(IJobBrowseProviderOperations provider) : IJobBrowseQueryPort
{
	public async Task<JobNodeDetailResult> GetNodeAsync(JobNodeId? nodeId, CancellationToken cancellationToken = default)
	{
		await using var context = provider.CreateContext();

		JobNodeEntity node;
		if (nodeId is JobNodeId id) {
			node = await context.Set<JobNodeEntity>().AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, cancellationToken)
					   .ConfigureAwait(false)
				   ?? throw new EntityNotFoundException($"Job node {id} does not exist.");
		} else {
			// A tree with no root is a miss like any other id that does not resolve -- a system whose
			// tree has not been bootstrapped yet, not a broken one -- so it reaches callers as the same
			// exception rather than as the sequence-empty fault SingleAsync would raise.
			node = await context.Set<JobNodeEntity>().AsNoTracking()
					   .SingleOrDefaultAsync(n => n.ParentId == null, cancellationToken).ConfigureAwait(false)
				   ?? throw new EntityNotFoundException("The job tree has no root node.");
		}

		var ancestors = await LoadAncestorsAsync(context, node, cancellationToken).ConfigureAwait(false);
		var nodeResult = await JobNodeStructuralProjection.ToResultAsync(context, node, cancellationToken).ConfigureAwait(false);

		return new() { Node = nodeResult, Ancestors = [.. ancestors] };
	}

	public async Task<EquatableArray<JobNodeSummaryResult>> GetChildrenAsync(
		JobNodeId parentId, OwnershipFilter ownership, JobArchiveFilter archiveFilter,
		int offset = 0, int? limit = null, CancellationToken cancellationToken = default)
	{
		await using var context = provider.CreateContext();

		var parentExists = await context.Set<JobNodeEntity>().AsNoTracking().AnyAsync(n => n.Id == parentId, cancellationToken)
			.ConfigureAwait(false);
		if (!parentExists) {
			throw new EntityNotFoundException($"Job node {parentId} does not exist.");
		}

		var query = ApplyFilters(context.Set<JobNodeEntity>().AsNoTracking().Where(n => n.ParentId == parentId), ownership, archiveFilter);
		var results = await LoadSummariesAsync(context, query, offset, limit, cancellationToken).ConfigureAwait(false);
		return EquatableArray.CopyOf(results);
	}

	public async Task<EquatableArray<JobNodeSummaryResult>> SearchJobNodesAsync(
		string searchText, OwnershipFilter ownership, JobArchiveFilter archiveFilter,
		int offset = 0, int? limit = null, CancellationToken cancellationToken = default)
	{
		await using var context = provider.CreateContext();

		var lowerSearchText = searchText.ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // this predicate is an EF expression tree translated to SQL LOWER()/LIKE by the provider, never executed by the CLR -- current-culture concerns don't apply
		var query = ApplyFilters(
			context.Set<JobNodeEntity>().AsNoTracking().Where(n => n.Description.ToLower().Contains(lowerSearchText)),
			ownership, archiveFilter);
#pragma warning restore CA1304, CA1311, CA1862
		var results = await LoadSummariesAsync(context, query, offset, limit, cancellationToken).ConfigureAwait(false);
		return EquatableArray.CopyOf(results);
	}

	public async Task<EquatableArray<JobNodeSummaryResult>> GetSummariesByIdsAsync(
		EquatableArray<JobNodeId> ids, CancellationToken cancellationToken = default)
	{
		if (ids.Count == 0) {
			return [];
		}

		await using var context = provider.CreateContext();

		var idList = ids.ToList();
		var query = context.Set<JobNodeEntity>().AsNoTracking().Where(n => idList.Contains(n.Id));
		var results = await LoadSummariesAsync(context, query, 0, null, cancellationToken).ConfigureAwait(false);
		return EquatableArray.CopyOf(results);
	}

	public async Task<JobSubtreeQueryResult> GetSubtreeAsync(
		JobNodeId rootId, int maxDepth, OwnershipFilter ownership, JobArchiveFilter archiveFilter,
		CancellationToken cancellationToken = default)
	{
		if (maxDepth < 0 || maxDepth > JobSubtreeLimits.HardMaxDepth) {
			throw new ArgumentOutOfRangeException(
				nameof(maxDepth), maxDepth, $"Subtree depth must be between 0 and {JobSubtreeLimits.HardMaxDepth}.");
		}

		await using var context = provider.CreateContext();
		// Repeatable-read pins one snapshot across the bounded-subtree recursive query and the
		// follow-up shaped-detail query below -- without it, a concurrent move/decompose committing
		// between the two statements can shift which ids the second statement sees (ADR 0039's
		// "coherent snapshot" requirement).
		await using var transaction = await context.Database.BeginTransactionAsync(
			IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

		var rootExists = await context.Set<JobNodeEntity>().AsNoTracking().AnyAsync(n => n.Id == rootId, cancellationToken)
			.ConfigureAwait(false);
		if (!rootExists) {
			throw new EntityNotFoundException($"Job node {rootId} does not exist.");
		}

		var bounded = await JobNodeHierarchyQueries.GetBoundedSubtreeAsync(
			context, rootId.Value, maxDepth, JobSubtreeLimits.BreadthCap, cancellationToken).ConfigureAwait(false);
		var rows = await LoadSubtreeRowsAsync(context, rootId, bounded, ownership, archiveFilter, cancellationToken).ConfigureAwait(false);
		// Materialize the complete subtree once, then evaluate it bottom-up into a request-scoped cache.
		// Every node receives exactly one cached result even when several displayed ancestor branches
		// depend on it; the display depth cap must not make a deep unfinished leaf look completed.
		var achievementRows = await JobNodeHierarchyQueries
			.GetSubtreeAchievementsAsync(context, rootId.Value, cancellationToken).ConfigureAwait(false);
		var childIdsByParent = achievementRows
			.Where(row => row.ParentId is not null)
			.GroupBy(row => row.ParentId!.Value)
			.ToDictionary(group => group.Key, group => group.Select(row => new JobNodeId(row.Id)).ToArray());
		var hierarchy = achievementRows.ToDictionary(
			row => new JobNodeId(row.Id),
			row => new HierarchyNode(
				new(row.Id),
				row.ParentId.HasValue ? new JobNodeId(row.ParentId.Value) : null,
				childIdsByParent.TryGetValue(row.Id, out var childIds) ? [.. childIds] : [],
				row.AchievementId.HasValue ? (Achievement)row.AchievementId.Value : null));
		var succeededByNodeId = AchievementCalculator.EvaluateSubtree(rootId, hierarchy);

		BranchAchievement? BranchAchievementFor(JobNodeSubtreeRow row)
		{
			if (row.Kind is NodeKind.Leaf) {
				return null;
			}

			return succeededByNodeId[row.Id] ? BranchAchievement.Success : BranchAchievement.Unfinished;
		}

		var enrichedRows = rows.Select(row => row with { BranchAchievement = BranchAchievementFor(row) }).ToArray();
		var rootAchievement = enrichedRows.Single(row => row.Id == rootId).BranchAchievement;
		return new() { Rows = EquatableArray.CopyOf(enrichedRows), RootAchievement = rootAchievement };
	}

	public async Task<BranchAchievement> GetSubtreeAchievementAsync(JobNodeId rootId, CancellationToken cancellationToken = default)
	{
		await using var context = provider.CreateContext();

		var rootExists = await context.Set<JobNodeEntity>().AsNoTracking()
			.AnyAsync(node => node.Id == rootId, cancellationToken)
			.ConfigureAwait(false);
		if (!rootExists) {
			throw new EntityNotFoundException($"Job node {rootId} does not exist.");
		}

		return await LoadSubtreeAchievementAsync(context, rootId, cancellationToken).ConfigureAwait(false);
	}

	public async Task<SubtreeImpactResult> GetSubtreeImpactAsync(JobNodeId rootId, CancellationToken cancellationToken = default)
	{
		await using var context = provider.CreateContext();

		var impact = await SubtreeImpactComputation.ComputeAsync(context, rootId, cancellationToken).ConfigureAwait(false);

		return SubtreeImpactProjection.ToResult(impact);
	}

	private async Task<BranchAchievement> LoadSubtreeAchievementAsync(
		DbContext context, JobNodeId rootId, CancellationToken cancellationToken)
	{
		var achieved = await provider.IsSubtreeSucceededAsync(context, rootId.Value, cancellationToken).ConfigureAwait(false);
		return achieved ? BranchAchievement.Success : BranchAchievement.Unfinished;
	}

	private static async Task<List<JobNodeAncestorResult>> LoadAncestorsAsync(
		DbContext context, JobNodeEntity node, CancellationToken cancellationToken)
	{
		var ancestors = new List<JobNodeAncestorResult>();
		if (node.ParentId is null) {
			return ancestors;
		}

		var chain = await JobNodeHierarchyQueries.GetAncestorChainAsync(context, node.Id.Value, cancellationToken).ConfigureAwait(false);
		var parentById = chain.ToDictionary(row => new JobNodeId(row.Id),
			row => row.ParentId.HasValue ? new JobNodeId(row.ParentId.Value) : (JobNodeId?)null);

		var ancestorIds = new List<JobNodeId>();
		var current = node.ParentId;
		while (current is JobNodeId ancestorId) {
			ancestorIds.Add(ancestorId);
			current = parentById.GetValueOrDefault(ancestorId);
		}

		ancestorIds.Reverse();

		var ancestorEntities = await context.Set<JobNodeEntity>().AsNoTracking()
			.Where(n => ancestorIds.Contains(n.Id))
			.Select(n => new
			{
				n.Id,
				n.Description,
				n.ParentId,
				HasChildren = context.Set<JobNodeEntity>().Any(c => c.ParentId == n.Id),
			})
			.ToDictionaryAsync(n => n.Id, cancellationToken).ConfigureAwait(false);

		ancestors.AddRange(ancestorIds.Select(id => {
			var entity = ancestorEntities[id];
			return new JobNodeAncestorResult(
				entity.Id,
				entity.Description,
				JobNodeStructuralResults.DeriveKind(entity.ParentId, entity.HasChildren));
		}));

		return ancestors;
	}

	private static IQueryable<JobNodeEntity> ApplyFilters(
		IQueryable<JobNodeEntity> query, OwnershipFilter ownership, JobArchiveFilter archiveFilter)
	{
		query = ownership.Kind switch {
			OwnershipFilterKind.All => query,
			OwnershipFilterKind.Unassigned => query.Where(n => n.OwnerUserId == null),
			OwnershipFilterKind.OwnedBy => query.Where(n => n.OwnerUserId == ownership.OwnerUserId!.Value),
			_ => throw new InvalidOperationException($"Unrecognised ownership filter kind: {ownership.Kind}."),
		};

		return archiveFilter switch {
			JobArchiveFilter.ActiveOnly => query.Where(n => n.ArchivedAt == null),
			JobArchiveFilter.ArchivedOnly => query.Where(n => n.ArchivedAt != null),
			JobArchiveFilter.All => query,
			_ => throw new ArgumentOutOfRangeException(nameof(archiveFilter), archiveFilter, null),
		};
	}

	private static async Task<List<JobNodeSummaryResult>> LoadSummariesAsync(
		DbContext context, IQueryable<JobNodeEntity> query, int offset, int? limit, CancellationToken cancellationToken)
	{
		var shaped = query.Select(n => new
		{
			n.Id,
			n.ParentId,
			n.Description,
			n.OwnerUserId,
			n.Priority,
			n.ArchivedAt,
			HasChildren = context.Set<JobNodeEntity>().Any(c => c.ParentId == n.Id),
			// No separate leaf_work existence probe: achievement_id is NOT NULL and job_node_id is the
			// primary key, so a non-null projected Achievement already means "this node holds LeafWork".
			Achievement = context.Set<LeafWorkEntity>()
				.Where(lw => lw.JobNodeId == n.Id).Select(lw => (Achievement?)lw.Achievement).FirstOrDefault(),
		});

		var ordered = shaped.OrderBy(n => n.Id).Skip(offset);
		var paged = limit.HasValue ? ordered.Take(limit.Value) : ordered;
		var rows = await paged.ToListAsync(cancellationToken).ConfigureAwait(false);

		return rows.Select(r => new JobNodeSummaryResult {
			Id = r.Id,
			ParentId = r.ParentId,
			Kind = JobNodeStructuralResults.DeriveKind(r.ParentId, r.HasChildren),
			Description = r.Description,
			OwnerUserId = r.OwnerUserId,
			Priority = r.Priority,
			ArchivedAt = r.ArchivedAt,
			HasChildren = r.HasChildren,
			HasLeafWork = r.Achievement is not null,
			Achievement = r.Achievement,
		}).ToList();
	}

	private static async Task<List<JobNodeSubtreeRow>> LoadSubtreeRowsAsync(
		DbContext context, JobNodeId rootId, IReadOnlyList<BoundedSubtreeRow> bounded, OwnershipFilter ownership,
		JobArchiveFilter archiveFilter, CancellationToken cancellationToken)
	{
		var idList = bounded.Select(r => new JobNodeId(r.Id)).ToList();
		var depthById = bounded.ToDictionary(r => new JobNodeId(r.Id), r => r.Depth);
		var expandedById = bounded.ToDictionary(r => new JobNodeId(r.Id), r => r.WasExpanded);

		var shaped = await context.Set<JobNodeEntity>().AsNoTracking()
			.Where(n => idList.Contains(n.Id))
			.Select(n => new
			{
				n.Id,
				n.ParentId,
				n.Description,
				n.OwnerUserId,
				n.Priority,
				n.NeededStart,
				n.NeededFinish,
				n.ArchivedAt,
				HasChildren = context.Set<JobNodeEntity>().Any(c => c.ParentId == n.Id),
				// Derived from Achievement below, not probed separately -- see LoadSummariesAsync.
				Achievement = context.Set<LeafWorkEntity>()
					.Where(lw => lw.JobNodeId == n.Id).Select(lw => (Achievement?)lw.Achievement).FirstOrDefault(),
				HasSessionHistory = context.Set<WorkSessionEntity>().Any(session => session.LeafWorkId == n.Id),
				HasUnacknowledgedRequest = context.Set<JobRequestEntity>()
					.Any(request => request.JobNodeId == n.Id && request.AcknowledgedAt == null),
			})
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		var matchesById = shaped.ToDictionary(r => r.Id, r => MatchesFilter(r.OwnerUserId, r.ArchivedAt, ownership, archiveFilter));

		var childrenByParent = shaped
			.Where(r => r.ParentId is JobNodeId parentId && idList.Contains(parentId))
			.GroupBy(r => r.ParentId!.Value)
			.ToDictionary(g => g.Key, g => g.Select(r => r.Id).ToList());

		var keepById = new Dictionary<JobNodeId, bool>();
		foreach (var row in shaped.OrderByDescending(r => depthById[r.Id])) {
			var descendantMatches = childrenByParent.TryGetValue(row.Id, out var childIds) && childIds.Any(c => keepById[c]);
			// The root is always shown regardless of filter match -- Browse is rooted at it, so
			// dropping it here would leave GetSubtreeAsync's rootRow lookup with nothing to find.
			keepById[row.Id] = matchesById[row.Id] || descendantMatches || row.Id == rootId;
		}

		return shaped
			.Where(r => keepById[r.Id])
			.OrderBy(r => r.Id.Value)
			.Select(r => new JobNodeSubtreeRow {
				Id = r.Id,
				ParentId = r.ParentId,
				Kind = JobNodeStructuralResults.DeriveKind(r.ParentId, r.HasChildren),
				Depth = depthById[r.Id],
				Description = r.Description,
				OwnerUserId = r.OwnerUserId,
				Priority = r.Priority,
				NeededStart = r.NeededStart,
				NeededFinish = r.NeededFinish,
				ArchivedAt = r.ArchivedAt,
				HasChildren = r.HasChildren,
				HasLeafWork = r.Achievement is not null,
				Achievement = r.Achievement,
				HasSessionHistory = r.HasSessionHistory,
				HasUnacknowledgedRequest = r.HasUnacknowledgedRequest,
				HasUnexpandedChildren = r.HasChildren && !expandedById[r.Id],
				MatchesFilter = matchesById[r.Id],
			})
			.ToList();
	}

	private static bool MatchesFilter(AppUserId? ownerUserId, Instant? archivedAt, OwnershipFilter ownership, JobArchiveFilter archiveFilter)
	{
		var ownershipMatch = ownership.Kind switch {
			OwnershipFilterKind.All => true,
			OwnershipFilterKind.Unassigned => ownerUserId is null,
			OwnershipFilterKind.OwnedBy => ownerUserId == ownership.OwnerUserId!.Value,
			_ => throw new InvalidOperationException($"Unrecognised ownership filter kind: {ownership.Kind}."),
		};

		var archiveMatch = archiveFilter switch {
			JobArchiveFilter.ActiveOnly => archivedAt is null,
			JobArchiveFilter.ArchivedOnly => archivedAt is not null,
			JobArchiveFilter.All => true,
			_ => throw new ArgumentOutOfRangeException(nameof(archiveFilter), archiveFilter, null),
		};

		return ownershipMatch && archiveMatch;
	}
}
