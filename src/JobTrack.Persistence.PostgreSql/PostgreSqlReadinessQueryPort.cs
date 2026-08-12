namespace JobTrack.Persistence.PostgreSql;

using System.Data;
using Abstractions;
using Application.Ports;
using Domain.Hierarchy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Shared;
using Shared.Entities;

internal sealed class PostgreSqlReadinessQueryPort : IReadinessQueryPort
{
	private readonly NpgsqlDataSource dataSource;
	private readonly IReadOnlyList<IInterceptor> interceptors = [];

	public PostgreSqlReadinessQueryPort(NpgsqlDataSource dataSource) => this.dataSource = dataSource;

	internal PostgreSqlReadinessQueryPort(NpgsqlDataSource dataSource, IReadOnlyList<IInterceptor> interceptors)
		: this(dataSource) =>
		this.interceptors = interceptors;

	public async Task<ReadinessQueryResult> GetReadinessInputsAsync(
		JobNodeId nodeId, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
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
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(
			IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
		var ancestorRows = await LoadAncestorChainsAsync(context, nodeIds, cancellationToken).ConfigureAwait(false);
		var result = await LoadAsync(context, ancestorRows, cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return result;
	}

	private PostgreSqlJobTrackDbContext CreateContext()
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, provider => provider.UseNodaTime());
		if (interceptors.Count > 0) {
			options = options.AddInterceptors(interceptors);
		}

		return new(options.Options);
	}

	/// <summary>
	///     Every requested node's own ancestor chain up to the true root, in one round trip, via the same
	///     <c>job_node_ancestor_chains</c> stored function <c>AwaitingProgressQueryAssembly</c> already
	///     uses for its own batch of unfinished-leaf candidates.
	/// </summary>
	private static async Task<List<ReadinessAncestorRow>> LoadAncestorChainsAsync(
		PostgreSqlJobTrackDbContext context, IReadOnlyCollection<JobNodeId> nodeIds, CancellationToken cancellationToken)
	{
		var nodeIdValues = nodeIds.Select(id => id.Value).ToArray();
		return await context.Database.SqlQuery<ReadinessAncestorRow>(
			$"""
			 SELECT id AS "Id", parent_id AS "ParentId"
			 FROM job_node_ancestor_chains({nodeIdValues})
			 """).ToListAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task<ReadinessQueryResult> LoadAsync(
		PostgreSqlJobTrackDbContext context, IReadOnlyCollection<ReadinessAncestorRow> ancestors, CancellationToken cancellationToken)
	{
		var ancestorIds = ancestors.Select(row => new JobNodeId(row.Id)).ToArray();
		var edges = await context.Set<JobPrerequisiteEntity>().AsNoTracking()
								 .Where(edge => ancestorIds.Contains(edge.ToId))
								 .Select(edge => new PrerequisiteEdge(edge.FromId, edge.ToId)).ToListAsync(cancellationToken).ConfigureAwait(false);
		var nodes = ancestors.ToDictionary(
			row => new JobNodeId(row.Id),
			row => new HierarchyNode(new(row.Id), row.ParentId is long parentId ? new JobNodeId(parentId) : null, [], null));

		// Every distinct required job, including one already present as an ancestor-chain stub: those
		// stubs deliberately carry no children and no leaf achievement, so treating an existing key as
		// "already loaded" made AchievementCalculator read a satisfied prerequisite as unachieved and
		// reported its dependents blocked. In the batch form the requested nodes are siblings/cousins
		// of one another, so a required job is routinely one of them.
		var requiredJobIds = edges.Select(edge => edge.RequiredJobId).Distinct().ToArray();
		if (requiredJobIds.Length > 0) {
			var requiredJobIdValues = requiredJobIds.Select(id => id.Value).ToArray();
			var subtree = await context.Database.SqlQuery<ReadinessSubtreeRow>(
				$"""
				 SELECT DISTINCT subtree.id AS "Id", subtree.parent_id AS "ParentId",
				        leaf.achievement_id AS "AchievementId"
				 FROM job_node_subtrees({requiredJobIdValues}) subtree
				 LEFT JOIN leaf_work leaf ON leaf.job_node_id = subtree.id
				 """).ToListAsync(cancellationToken).ConfigureAwait(false);
			AddSubtreeNodes(nodes, subtree);
		}

		return new() {
			NodesById = EquatableDictionaryFactory.CopyOf(nodes),
			Prerequisites = EquatableArray.CopyOf(edges),
		};
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

/// <summary>One row of <see cref="PostgreSqlReadinessQueryPort.LoadAncestorChainsAsync" />.</summary>
internal sealed record ReadinessAncestorRow(long Id, long? ParentId);

internal sealed record ReadinessSubtreeRow(long Id, long? ParentId, short? AchievementId);
