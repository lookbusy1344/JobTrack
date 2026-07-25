namespace JobTrack.Persistence.Sqlite;

using System.Data;
using System.Text.Json;
using Abstractions;
using Application.Ports;
using Domain.Hierarchy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shared;
using Shared.Entities;

internal sealed class SqliteReadinessQueryPort : IReadinessQueryPort
{
	private readonly string connectionString;
	private readonly IReadOnlyList<IInterceptor> interceptors = [];

	public SqliteReadinessQueryPort(string connectionString) => this.connectionString = connectionString;

	internal SqliteReadinessQueryPort(string connectionString, IReadOnlyList<IInterceptor> interceptors)
		: this(connectionString) =>
		this.interceptors = interceptors;

	public async Task<ReadinessQueryResult> GetReadinessInputsAsync(
		JobNodeId nodeId, CancellationToken cancellationToken = default)
	{
		await using var context = SqliteDbContextFactory.CreateContext(connectionString, interceptors);
		await using var transaction = await context.Database.BeginTransactionAsync(
			IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
		var ancestors = await JobNodeHierarchyQueries.GetAncestorChainAsync(context, nodeId.Value, cancellationToken)
			.ConfigureAwait(false);
		if (ancestors.Count == 0) {
			throw new EntityNotFoundException($"Job node {nodeId} does not exist.");
		}

		var ancestorRows = ancestors.Select(row => new ReadinessAncestorRow(row.Id, row.ParentId)).ToList();
		var result = await LoadAsync(context, ancestorRows, cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return result;
	}

	public async Task<ReadinessQueryResult> GetReadinessInputsForNodesAsync(
		IReadOnlyCollection<JobNodeId> nodeIds, CancellationToken cancellationToken = default)
	{
		await using var context = SqliteDbContextFactory.CreateContext(connectionString, interceptors);
		await using var transaction = await context.Database.BeginTransactionAsync(
			IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
		var ancestorRows = await LoadAncestorChainsAsync(context, nodeIds, cancellationToken).ConfigureAwait(false);
		var result = await LoadAsync(context, ancestorRows, cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return result;
	}

	/// <summary>
	///     Every requested node's own ancestor chain up to the true root, in one round trip, via a
	///     parameterized recursive CTE (no stored functions on SQLite) -- mirrors
	///     <c>AwaitingProgressQueryAssembly.LoadAncestorChainsAsync</c>'s established pattern.
	/// </summary>
	private static async Task<List<ReadinessAncestorRow>> LoadAncestorChainsAsync(
		DbContext context, IReadOnlyCollection<JobNodeId> nodeIds, CancellationToken cancellationToken)
	{
		var nodeIdValues = nodeIds.Select(id => id.Value).ToList();
		var nodeIdParameters = nodeIdValues.Select((_, index) => $"@nodeId{index}").ToArray();
		var sql = $"""
				   WITH RECURSIVE ancestors(id, parent_id) AS (
				       SELECT id, parent_id FROM job_node WHERE id IN ({string.Join(',', nodeIdParameters)})
				       UNION ALL
				       SELECT jn.id, jn.parent_id
				       FROM job_node jn
				       JOIN ancestors a ON jn.id = a.parent_id
				   )
				   SELECT DISTINCT id AS "Id", parent_id AS "ParentId" FROM ancestors
				   """;
		var parameters = nodeIdValues.Select((id, index) => (object)new SqliteParameter(nodeIdParameters[index], id)).ToArray();
		return await context.Database.SqlQueryRaw<ReadinessAncestorRow>(sql, parameters)
			.ToListAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task<ReadinessQueryResult> LoadAsync(
		DbContext context, IReadOnlyCollection<ReadinessAncestorRow> ancestors, CancellationToken cancellationToken)
	{
		var ancestorIds = ancestors.Select(row => new JobNodeId(row.Id)).ToArray();
		var edges = await context.Set<JobPrerequisiteEntity>().AsNoTracking()
			.Where(edge => ancestorIds.Contains(edge.ToId))
			.Select(edge => new PrerequisiteEdge(edge.FromId, edge.ToId)).ToListAsync(cancellationToken).ConfigureAwait(false);
		var nodes = ancestors.ToDictionary(
			row => new JobNodeId(row.Id),
			row => new HierarchyNode(new(row.Id), row.ParentId is long parentId ? new JobNodeId(parentId) : null, [], null));

		var requiredJobIds = edges.Select(edge => edge.RequiredJobId).Distinct().Where(id => !nodes.ContainsKey(id)).ToArray();
		if (requiredJobIds.Length > 0) {
			var requiredJobIdsJson = JsonSerializer.Serialize(requiredJobIds.Select(id => id.Value));
			var sql = """
					  WITH RECURSIVE roots(id) AS (
					      SELECT value FROM json_each(@requiredJobIds)
					  ), subtree(id) AS (
					      SELECT id FROM roots
					      UNION
					      SELECT node.id
					      FROM job_node node
					      JOIN subtree ON node.parent_id = subtree.id
					  )
					  SELECT subtree.id AS "Id", node.parent_id AS "ParentId",
					         leaf.achievement_id AS "AchievementId"
					  FROM subtree
					  JOIN job_node node ON node.id = subtree.id
					  LEFT JOIN leaf_work leaf ON leaf.job_node_id = subtree.id
					  """;
			var subtree = await context.Database.SqlQueryRaw<ReadinessSubtreeRow>(
					sql, new SqliteParameter("@requiredJobIds", requiredJobIdsJson))
				.ToListAsync(cancellationToken).ConfigureAwait(false);
			AddSubtreeNodes(nodes, subtree);
		}

		return new() { NodesById = EquatableDictionaryFactory.CopyOf(nodes), Prerequisites = EquatableArray.CopyOf(edges) };
	}

	private static void AddSubtreeNodes(Dictionary<JobNodeId, HierarchyNode> nodes, IReadOnlyCollection<ReadinessSubtreeRow> subtree)
	{
		var childrenByParent = subtree.Where(row => row.ParentId is not null).GroupBy(row => row.ParentId!.Value)
			.ToDictionary(group => group.Key, group => EquatableArray.CopyOf(group.Select(row => new JobNodeId(row.Id))));
		foreach (var row in subtree) {
			nodes[new(row.Id)] = new(new(row.Id), row.ParentId is long parentId ? new JobNodeId(parentId) : null,
				childrenByParent.GetValueOrDefault(row.Id, []), row.AchievementId is short achievementId ? (Achievement)achievementId : null);
		}
	}
}

/// <summary>One row of <see cref="SqliteReadinessQueryPort.LoadAncestorChainsAsync" />.</summary>
internal sealed record ReadinessAncestorRow(long Id, long? ParentId);

internal sealed record ReadinessSubtreeRow(long Id, long? ParentId, short? AchievementId);
