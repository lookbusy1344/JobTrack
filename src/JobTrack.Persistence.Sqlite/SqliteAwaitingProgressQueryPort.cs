namespace JobTrack.Persistence.Sqlite;

using System.Data;
using System.Text.Json;
using Abstractions;
using Application.Ports;
using Domain.Hierarchy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using Shared;
using Shared.Entities;

/// <summary>
///     SQLite implementation of <see cref="IAwaitingProgressQueryPort" />. One
///     <see cref="SqliteJobTrackDbContext" /> per call, read-only throughout. Materializes only the
///     requested filter's own ownership/subtree/search/paged candidate page (2026-07-25
///     scalability-follow-up plan §2.1, on top of the 2026-07-24 §2.2 step 4 narrowing to
///     currently-unfinished leaves) -- never every unfinished leaf in the installation -- plus each
///     candidate's own ancestor chain (for inherited-prerequisite discovery, same elevated-scope shape
///     ADR 0017's cost-read narrowing already established) and the prerequisite edges reachable from
///     that scope. See <see cref="AwaitingProgressQueryAssembly.LoadAsync" /> for the exact
///     construction.
/// </summary>
internal sealed class SqliteAwaitingProgressQueryPort : IAwaitingProgressQueryPort
{
	private readonly string connectionString;
	private readonly IReadOnlyList<IInterceptor> interceptors = [];

	public SqliteAwaitingProgressQueryPort(string connectionString) => this.connectionString = connectionString;

	internal SqliteAwaitingProgressQueryPort(string connectionString, IReadOnlyList<IInterceptor> interceptors)
		: this(connectionString) =>
		this.interceptors = interceptors;

	public async Task<AwaitingProgressQueryResult> GetAwaitingProgressInputsAsync(
		AwaitingProgressQueryFilter filter, CancellationToken cancellationToken = default)
	{
		await using var context = SqliteDbContextFactory.CreateContext(connectionString, interceptors);
		await using var transaction = await context.Database.BeginTransactionAsync(
			IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
		var result = await AwaitingProgressQueryAssembly.LoadAsync(context, filter, cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return result;
	}
}

/// <summary>
///     The narrowed-load assembly behind <see cref="SqliteAwaitingProgressQueryPort" />, mirrored
///     (necessarily duplicated, not literally shared -- 2026-07-24 remediation plan §2.6) by
///     PostgreSQL's own <c>AwaitingProgressQueryAssembly</c>. SQLite has no stored functions, so the
///     ancestor-chain lookup is a parameterized recursive CTE (mirroring <c>SqliteControlledLeafQuery</c>'s
///     established pattern) and each distinct required job's recursive achievement is resolved through
///     <see cref="JobNodeHierarchyQueries.IsSubtreeAchievedSqliteAsync" />, the same shared helper
///     <see cref="SqliteJobBrowseQueryPort" />'s single-node subtree-achievement check already uses.
/// </summary>
internal static class AwaitingProgressQueryAssembly
{
	/// <summary>See PostgreSQL's <c>AwaitingProgressQueryAssembly.NotACandidateSentinel</c> for why this must be terminal.</summary>
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
				nodesById[id] = new(id, row.ParentId is long parentId ? new JobNodeId(parentId) : null, [], NotACandidateSentinel);
			}
		}

		var inScopeIds = nodesById.Keys.ToList();
		var edges = await context.Set<JobPrerequisiteEntity>().AsNoTracking()
			.Where(edge => inScopeIds.Contains(edge.ToId))
			.Select(edge => new { edge.FromId, edge.ToId })
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		var requiredJobIds = edges.Select(edge => edge.FromId).Distinct().Where(id => !candidateIds.Contains(id)).ToList();
		var succeededById = await LoadRequiredJobAchievementsAsync(context, requiredJobIds, cancellationToken).ConfigureAwait(false);
		foreach (var requiredJobId in requiredJobIds) {
			var succeeded = succeededById[requiredJobId];
			var existingParentId = nodesById.TryGetValue(requiredJobId, out var existing) ? existing.ParentId : null;
			nodesById[requiredJobId] = new(requiredJobId, existingParentId, [], succeeded ? Achievement.Success : NotACandidateSentinel);
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

	private static async Task<Dictionary<JobNodeId, bool>> LoadRequiredJobAchievementsAsync(
		DbContext context, List<JobNodeId> requiredJobIds, CancellationToken cancellationToken)
	{
		if (requiredJobIds.Count == 0) {
			return [];
		}

		var requiredJobIdsJson = JsonSerializer.Serialize(requiredJobIds.Select(id => id.Value));
		var rows = await context.Database.SqlQueryRaw<AwaitingProgressSucceededRow>(
				"""
				WITH RECURSIVE roots(id) AS (
				    SELECT value FROM json_each(@requiredJobIds)
				), subtree(origin_id, id) AS (
				    SELECT id, id FROM roots
				    UNION ALL
				    SELECT subtree.origin_id, child.id
				    FROM job_node child JOIN subtree ON child.parent_id = subtree.id
				)
				SELECT roots.id AS "Id", NOT EXISTS (
				    SELECT 1 FROM subtree
				    WHERE subtree.origin_id = roots.id
				      AND NOT EXISTS (SELECT 1 FROM job_node child WHERE child.parent_id = subtree.id)
				      AND NOT EXISTS (
				          SELECT 1 FROM leaf_work
				          WHERE leaf_work.job_node_id = subtree.id AND leaf_work.achievement_id = @successAchievementId
				      )
				) AS "Succeeded"
				FROM roots
				""",
				new SqliteParameter("@requiredJobIds", requiredJobIdsJson),
				new SqliteParameter("@successAchievementId", (short)Achievement.Success))
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		return rows.ToDictionary(row => new JobNodeId(row.Id), row => row.Succeeded);
	}

	/// <summary>
	///     <paramref name="filter" />'s own page of currently-unfinished leaves -- see PostgreSQL's twin
	///     for the full rationale, including the exact ordering pushed into the query.
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
			filteredNodes = filteredNodes.Where(node => SqliteTextSearchFunctions.ContainsOrdinalIgnoreCase(node.Description, filter.SearchText));
		}

		// Ordering runs against the entity/join columns directly, before the final projection into
		// AwaitingProgressCandidate -- SQLite's EF provider (unlike PostgreSQL's) cannot translate an
		// OrderBy over a property of an already-constructed record.
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
	///     A composable relation containing every node in <paramref name="rootId" />'s subtree. The
	///     candidate query composes this parameterized recursive CTE as an <c>EXISTS</c> predicate, so
	///     subtree ids are never materialized in the application process.
	/// </summary>
	private static IQueryable<JobNodeEntity> LoadSubtreeNodes(DbContext context, JobNodeId rootId) =>
		context.Set<JobNodeEntity>().FromSqlRaw(
				"""
				WITH RECURSIVE subtree(id) AS (
				    SELECT id FROM job_node WHERE id = @rootId
				    UNION ALL
				    SELECT node.id
				    FROM job_node node
				    JOIN subtree ON node.parent_id = subtree.id
				)
				SELECT node.*
				FROM subtree
				JOIN job_node node ON node.id = subtree.id
				""",
				new SqliteParameter("@rootId", rootId.Value))
			.AsNoTracking();

	/// <summary>
	///     Every candidate's own ancestor chain up to the true root, via a parameterized recursive CTE
	///     (no stored functions on SQLite) -- mirrors <see cref="SqliteControlledLeafQuery" />'s
	///     established pattern.
	/// </summary>
	private static async Task<List<AwaitingProgressAncestorRow>> LoadAncestorChainsAsync(
		DbContext context, IReadOnlyCollection<JobNodeId> candidateIds, CancellationToken cancellationToken)
	{
		var candidateIdValues = candidateIds.Select(id => id.Value).ToList();
		var candidateIdParameters = candidateIdValues.Select((_, index) => $"@candidateId{index}").ToArray();
		var sql = $"""
				   WITH RECURSIVE ancestors(id, parent_id) AS (
				       SELECT id, parent_id FROM job_node WHERE id IN ({string.Join(',', candidateIdParameters)})
				       UNION ALL
				       SELECT jn.id, jn.parent_id
				       FROM job_node jn
				       JOIN ancestors a ON jn.id = a.parent_id
				   )
				   SELECT DISTINCT id AS "Id", parent_id AS "ParentId" FROM ancestors
				   """;
		var parameters = candidateIdValues.Select((id, index) => (object)new SqliteParameter(candidateIdParameters[index], id)).ToArray();
		return await context.Database.SqlQueryRaw<AwaitingProgressAncestorRow>(sql, parameters)
			.ToListAsync(cancellationToken).ConfigureAwait(false);
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

/// <summary>One recursively resolved required-job achievement for the narrowed Awaiting Progress load.</summary>
internal sealed record AwaitingProgressSucceededRow(long Id, bool Succeeded);
