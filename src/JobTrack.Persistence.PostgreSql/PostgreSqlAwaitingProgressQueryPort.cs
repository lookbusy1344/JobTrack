namespace JobTrack.Persistence.PostgreSql;

using System.Data;
using Abstractions;
using Application.Ports;
using Domain.Hierarchy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using Npgsql;
using Shared.Entities;

/// <summary>
///     PostgreSQL implementation of <see cref="IAwaitingProgressQueryPort" />. One
///     <see cref="PostgreSqlJobTrackDbContext" /> per call, read-only throughout. Materializes only the
///     requested filter's own ownership/subtree/search/paged candidate page (2026-07-25
///     scalability-follow-up plan §2.1, on top of the 2026-07-24 §2.2 step 4 narrowing to
///     currently-unfinished leaves) -- never every unfinished leaf in the installation -- plus each
///     candidate's own ancestor chain (for inherited-prerequisite discovery, same elevated-scope shape
///     ADR 0017's cost-read narrowing already established) and the prerequisite edges reachable from
///     that scope. See <see cref="AwaitingProgressQueryAssembly.LoadAsync" /> for the exact
///     construction.
/// </summary>
internal sealed class PostgreSqlAwaitingProgressQueryPort : IAwaitingProgressQueryPort
{
	private readonly NpgsqlDataSource dataSource;
	private readonly IReadOnlyList<IInterceptor> interceptors = [];

	public PostgreSqlAwaitingProgressQueryPort(NpgsqlDataSource dataSource) => this.dataSource = dataSource;

	internal PostgreSqlAwaitingProgressQueryPort(NpgsqlDataSource dataSource, IReadOnlyList<IInterceptor> interceptors)
		: this(dataSource) =>
		this.interceptors = interceptors;

	public async Task<AwaitingProgressQueryResult> GetAwaitingProgressInputsAsync(
		AwaitingProgressQueryFilter filter, CancellationToken cancellationToken = default)
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, provider => provider.UseNodaTime());
		if (interceptors.Count > 0) {
			options = options.AddInterceptors(interceptors);
		}

		await using var context = new PostgreSqlJobTrackDbContext(options.Options);
		await using var transaction = await context.Database.BeginTransactionAsync(
			IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
		var result = await AwaitingProgressQueryAssembly.LoadAsync(context, filter, cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return result;
	}
}

/// <summary>
///     The narrowed-load assembly behind <see cref="PostgreSqlAwaitingProgressQueryPort" />, mirrored
///     (necessarily duplicated, not literally shared -- 2026-07-24 remediation plan §2.6) by SQLite's
///     own <c>AwaitingProgressQueryAssembly</c>.
/// </summary>
internal static class AwaitingProgressQueryAssembly
{
	/// <summary>
	///     Placeholder achievement for a narrowed-load node that is not itself an unfinished-leaf
	///     candidate (an ancestor waypoint, or a required job resolved as not-succeeded) -- any terminal
	///     value works equally here, since <see cref="AchievementCalculator" /> only ever distinguishes
	///     <see cref="Achievement.Success" /> from everything else, and this specific value is never
	///     otherwise observed (see the two call sites' own remarks for why it must not be
	///     <see cref="Achievement.Waiting" />/<see cref="Achievement.InProgress" />).
	/// </summary>
	private const Achievement NotACandidateSentinel = Achievement.Cancelled;

	public static async Task<AwaitingProgressQueryResult> LoadAsync(
		DbContext context, AwaitingProgressQueryFilter filter, CancellationToken cancellationToken)
	{
		var candidates = await LoadCandidatesAsync(context, filter, cancellationToken).ConfigureAwait(false);
		if (candidates.Count == 0) {
			return new() {
				NodesById = EquatableDictionaryFactory.CopyOf(new Dictionary<JobNodeId, HierarchyNode>()),
				FactsById = EquatableDictionaryFactory.CopyOf(new Dictionary<JobNodeId, AwaitingProgressNodeFacts>()),
				Prerequisites = [],
			};
		}

		var candidateIds = new HashSet<JobNodeId>(candidates.Select(candidate => candidate.Id));
		var nodesById = new Dictionary<JobNodeId, HierarchyNode>();
		foreach (var candidate in candidates) {
			nodesById[candidate.Id] = new(candidate.Id, candidate.ParentId, [], candidate.Achievement);
		}

		var ancestorRows = await LoadAncestorChainsAsync(context, candidateIds, cancellationToken).ConfigureAwait(false);
		foreach (var row in ancestorRows) {
			var id = new JobNodeId(row.Id);
			if (!nodesById.ContainsKey(id)) {
				// AwaitingProgressCalculator re-derives its own candidate set by scanning nodesById for
				// IsUnfinishedLeaf (ChildIds.Count == 0 && achievement null/Waiting/InProgress) -- an
				// ancestor waypoint, kept here only for the ParentId walk, must not accidentally match
				// that shape. Any terminal (non-Waiting/InProgress) achievement defeats it; Success is
				// otherwise meaningless for a placeholder that is really a branch, not a leaf.
				nodesById[id] = new(id, row.ParentId is long parentId ? new JobNodeId(parentId) : null, [], NotACandidateSentinel);
			}
		}

		var inScopeIds = nodesById.Keys.ToList();
		var edges = await context.Set<JobPrerequisiteEntity>().AsNoTracking()
			.Where(edge => inScopeIds.Contains(edge.ToId))
			.Select(edge => new { edge.FromId, edge.ToId })
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		// A required job already represented as a genuine unfinished-leaf candidate keeps its own real
		// entry (correct by construction: an unfinished leaf never satisfies a prerequisite) -- only a
		// required job outside that set needs its achievement resolved, since it may be a branch (or a
		// finished leaf) this narrowed load never otherwise materialized.
		var requiredJobIds = edges.Select(edge => edge.FromId).Distinct().Where(id => !candidateIds.Contains(id)).ToList();
		if (requiredJobIds.Count > 0) {
			var succeededRows = await LoadRequiredJobAchievementsAsync(context, requiredJobIds, cancellationToken).ConfigureAwait(false);
			foreach (var row in succeededRows) {
				var id = new JobNodeId(row.Id);
				// AchievementCalculator.IsAchieved reads only this entry's own ChildIds/LeafAchievement
				// when nodeId is the traversal's own starting point (never a required job's real
				// children), so representing it as a childless node carrying the already-resolved
				// answer is exact regardless of its true structure. Any ParentId this id already
				// carries (it may double as another candidate's real ancestor) is preserved -- only
				// achievement resolution is overridden here.
				var existingParentId = nodesById.TryGetValue(id, out var existing) ? existing.ParentId : null;
				// Not-succeeded must still be a terminal achievement (see NotACandidateSentinel's own
				// remarks) -- Waiting/InProgress would risk this override id re-matching
				// IsUnfinishedLeaf if it is also some other candidate's real ancestor.
				nodesById[id] = new(id, existingParentId, [], row.Succeeded ? Achievement.Success : NotACandidateSentinel);
			}
		}

		var factsById = candidates.ToDictionary(
			candidate => candidate.Id,
			candidate => new AwaitingProgressNodeFacts(
				candidate.Id, candidate.Description, candidate.OwnerUserId, candidate.Priority,
				candidate.NeededStart, candidate.NeededFinish, candidate.ArchivedAt));

		return new() {
			NodesById = EquatableDictionaryFactory.CopyOf(nodesById),
			FactsById = EquatableDictionaryFactory.CopyOf(factsById),
			Prerequisites = [.. edges.Select(edge => new PrerequisiteEdge(edge.FromId, edge.ToId))],
		};
	}

	/// <summary>
	///     <paramref name="filter" />'s own page of currently-unfinished leaves: childless, not archived,
	///     either lacking <c>leaf_work</c> entirely or not yet in a terminal achievement -- exactly
	///     <see cref="AwaitingProgressCalculator" />'s own <c>IsUnfinishedLeaf</c> criterion -- further
	///     scoped by ownership/subtree-root/search-text and ordered by the exact descending-priority,
	///     ascending-deadline-nulls-last, ascending-id sequence the calculator used to apply in memory,
	///     then paged. All pushed into the query itself (EF-first) rather than filtered/ordered/paged in
	///     memory after loading every unfinished leaf in the installation.
	/// </summary>
	private static async Task<List<AwaitingProgressCandidate>> LoadCandidatesAsync(
		DbContext context, AwaitingProgressQueryFilter filter, CancellationToken cancellationToken)
	{
		var nodes = context.Set<JobNodeEntity>().AsNoTracking();
		var filteredNodes = nodes.Where(node => node.ParentId != null && node.ArchivedAt == null);

		filteredNodes = filter.Ownership.Kind switch {
			OwnershipFilterKind.All => filteredNodes,
			OwnershipFilterKind.Unassigned => filteredNodes.Where(node => node.OwnerUserId == null),
			OwnershipFilterKind.OwnedBy => filteredNodes.Where(node => node.OwnerUserId == filter.Ownership.OwnerUserId!.Value),
			_ => throw new InvalidOperationException($"Unrecognised ownership filter kind: {filter.Ownership.Kind}."),
		};

		if (filter.SubtreeRootId is JobNodeId subtreeRootId) {
			var subtreeNodes = LoadSubtreeNodes(context, subtreeRootId);
			filteredNodes = filteredNodes.Where(node =>
				nodes.Any(scope => scope.Id == subtreeRootId && scope.ParentId == null)
				|| subtreeNodes.Any(subtreeNode => subtreeNode.Id == node.Id));
		}

		if (!string.IsNullOrWhiteSpace(filter.SearchText)) {
			var lowerSearchText = filter.SearchText.ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // this predicate is an EF expression tree translated to SQL LOWER()/LIKE by the provider, never executed by the CLR -- current-culture concerns don't apply
			filteredNodes = filteredNodes.Where(node => node.Description.ToLower().Contains(lowerSearchText));
#pragma warning restore CA1304, CA1311, CA1862
		}

		// Ordering runs against the entity/join columns directly, before the final projection into
		// AwaitingProgressCandidate -- matches SQLite's provider requirement below, and keeps both
		// providers translating identically rather than relying on PostgreSQL's provider tolerating an
		// OrderBy over an already-constructed record.
		var query =
			from node in filteredNodes
			where !nodes.Any(child => child.ParentId == node.Id)
			join leaf in context.Set<LeafWorkEntity>().AsNoTracking() on node.Id equals leaf.JobNodeId into leafGroup
			from leaf in leafGroup.DefaultIfEmpty()
			let achievement = (Achievement?)leaf.Achievement
			where achievement == null || achievement == Achievement.Waiting || achievement == Achievement.InProgress
			select new
			{
				node.Id,
				node.ParentId,
				node.Description,
				node.OwnerUserId,
				node.Priority,
				node.NeededStart,
				node.NeededFinish,
				node.ArchivedAt,
				Achievement = achievement,
			};

		var ordered = query
			.OrderByDescending(candidate => candidate.Priority)
			.ThenBy(candidate => (candidate.NeededFinish ?? candidate.NeededStart) == null)
			.ThenBy(candidate => candidate.NeededFinish ?? candidate.NeededStart)
			.ThenBy(candidate => candidate.Id)
			.Skip(filter.Offset)
			.Take(filter.Limit)
			.Select(candidate => new AwaitingProgressCandidate(
				candidate.Id, candidate.ParentId, candidate.Description, candidate.OwnerUserId, candidate.Priority,
				candidate.NeededStart, candidate.NeededFinish, candidate.ArchivedAt, candidate.Achievement));

		return await ordered.ToListAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	///     A composable relation containing every node in <paramref name="rootId" />'s subtree,
	///     through the same canonical
	///     <c>job_node_subtrees</c> stored function <c>PostgreSqlCostQueryPort</c> already uses for
	///     subtree containment. The candidate query composes this as an <c>EXISTS</c> predicate, so
	///     subtree ids are never materialized in the application process.
	/// </summary>
	private static IQueryable<JobNodeEntity> LoadSubtreeNodes(DbContext context, JobNodeId rootId)
	{
		var rootIdValues = new[] { rootId.Value };
		return context.Set<JobNodeEntity>().FromSql(
			$"""
			 SELECT node.*
			 FROM job_node_subtrees({rootIdValues}) subtree
			 JOIN job_node node ON node.id = subtree.id
			 """).AsNoTracking();
	}

	/// <summary>
	///     Every candidate's own ancestor chain up to the true root (ADR 0017-shaped elevated scope,
	///     same reasoning as <c>CostQueryAssembly.ExtendAncestryAsync</c>): a prerequisite can be
	///     inherited from any ancestor, and subtree-membership filtering walks the same chain.
	/// </summary>
	private static async Task<List<AwaitingProgressAncestorRow>> LoadAncestorChainsAsync(
		DbContext context, IReadOnlyCollection<JobNodeId> candidateIds, CancellationToken cancellationToken)
	{
		var candidateIdValues = candidateIds.Select(id => id.Value).ToArray();
		return await context.Database.SqlQuery<AwaitingProgressAncestorRow>(
			$"""
			 SELECT id AS "Id", parent_id AS "ParentId"
			 FROM job_node_ancestor_chains({candidateIdValues})
			 """).ToListAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	///     Resolves each requested required job's recursive achievement (spec §5.2) through the
	///     existing <c>node_succeeded</c> stored function -- a branch's own subtree can be arbitrarily
	///     large and lie entirely outside the narrowed load, so this asks the database for the already-
	///     correct answer rather than materializing that subtree here.
	/// </summary>
	private static async Task<List<AwaitingProgressSucceededRow>> LoadRequiredJobAchievementsAsync(
		DbContext context, IReadOnlyCollection<JobNodeId> requiredJobIds, CancellationToken cancellationToken)
	{
		var requiredJobIdValues = requiredJobIds.Select(id => id.Value).ToArray();
		return await context.Database.SqlQuery<AwaitingProgressSucceededRow>(
			$"""
			 SELECT id AS "Id", node_succeeded(id) AS "Succeeded"
			 FROM unnest({requiredJobIdValues}) AS t(id)
			 """).ToListAsync(cancellationToken).ConfigureAwait(false);
	}
}

/// <summary>One row of <see cref="AwaitingProgressQueryAssembly.LoadCandidatesAsync" />.</summary>
internal sealed record AwaitingProgressCandidate(
	JobNodeId Id,
	JobNodeId? ParentId,
	string Description,
	AppUserId? OwnerUserId,
	Priority Priority,
	Instant? NeededStart,
	Instant? NeededFinish,
	Instant? ArchivedAt,
	Achievement? Achievement);

/// <summary>One row of <see cref="AwaitingProgressQueryAssembly.LoadAncestorChainsAsync" />.</summary>
internal sealed record AwaitingProgressAncestorRow(long Id, long? ParentId);

/// <summary>One row of <see cref="AwaitingProgressQueryAssembly.LoadRequiredJobAchievementsAsync" />.</summary>
internal sealed record AwaitingProgressSucceededRow(long Id, bool Succeeded);
