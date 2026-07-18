namespace JobTrack.Persistence.Sqlite;

using Abstractions;
using Application.Ports;
using Domain.Hierarchy;
using Microsoft.EntityFrameworkCore;
using Shared.Entities;

internal sealed class SqliteReadinessQueryPort(string connectionString) : IReadinessQueryPort
{
	public async Task<ReadinessQueryResult> GetReadinessInputsAsync(
		JobNodeId nodeId, CancellationToken cancellationToken = default)
	{
		await using var context = SqliteDbContextFactory.CreateContext(connectionString);
		var rows = await context.Set<JobNodeEntity>().AsNoTracking()
			.Select(node => new { node.Id, node.ParentId }).ToListAsync(cancellationToken).ConfigureAwait(false);
		if (!rows.Any(row => row.Id == nodeId)) {
			throw new EntityNotFoundException($"Job node {nodeId} does not exist.");
		}

		var achievements = await context.Set<LeafWorkEntity>().AsNoTracking()
			.ToDictionaryAsync(leaf => leaf.JobNodeId, leaf => leaf.Achievement, cancellationToken).ConfigureAwait(false);
		var children = rows.Where(row => row.ParentId is not null).GroupBy(row => row.ParentId!.Value)
			.ToDictionary(group => group.Key, group => EquatableArray.CopyOf(group.Select(row => row.Id)));
		var nodes = rows.ToDictionary(
			row => row.Id,
			row => new HierarchyNode(
				row.Id,
				row.ParentId,
				children.GetValueOrDefault(row.Id, []),
				achievements.TryGetValue(row.Id, out var achievement) ? achievement : null));
		var edges = await context.Set<JobPrerequisiteEntity>().AsNoTracking()
			.Select(edge => new PrerequisiteEdge(edge.FromId, edge.ToId)).ToListAsync(cancellationToken).ConfigureAwait(false);

		return new() { NodesById = EquatableDictionaryFactory.CopyOf(nodes), Prerequisites = EquatableArray.CopyOf(edges) };
	}
}
