namespace JobTrack.Persistence.Sqlite;

using Abstractions;
using Application.Ports;
using Domain.Hierarchy;
using Microsoft.EntityFrameworkCore;
using Shared.Entities;

/// <summary>
///     SQLite implementation of <see cref="IAwaitingProgressQueryPort" />. One
///     <see cref="SqliteJobTrackDbContext" /> per call, read-only throughout — same full-table-load
///     shape as <see cref="SqliteReadinessQueryPort" />, plus the display/filter/sort facts
///     <see cref="AwaitingProgressCalculator" /> needs.
/// </summary>
internal sealed class SqliteAwaitingProgressQueryPort(string connectionString) : IAwaitingProgressQueryPort
{
	public async Task<AwaitingProgressQueryResult> GetAwaitingProgressInputsAsync(CancellationToken cancellationToken = default)
	{
		await using var context = SqliteDbContextFactory.CreateContext(connectionString);

		var rows = await context.Set<JobNodeEntity>().AsNoTracking()
			.Select(node => new
			{
				node.Id,
				node.ParentId,
				node.Description,
				node.OwnerUserId,
				node.Priority,
				node.NeededStart,
				node.NeededFinish,
				node.ArchivedAt,
			})
			.ToListAsync(cancellationToken).ConfigureAwait(false);

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
		var facts = rows.ToDictionary(
			row => row.Id,
			row => new AwaitingProgressNodeFacts(
				row.Id, row.Description, row.OwnerUserId, row.Priority, row.NeededStart, row.NeededFinish, row.ArchivedAt));
		var edges = await context.Set<JobPrerequisiteEntity>().AsNoTracking()
			.Select(edge => new PrerequisiteEdge(edge.FromId, edge.ToId)).ToListAsync(cancellationToken).ConfigureAwait(false);

		return new() {
			NodesById = EquatableDictionaryFactory.CopyOf(nodes),
			FactsById = EquatableDictionaryFactory.CopyOf(facts),
			Prerequisites = EquatableArray.CopyOf(edges),
		};
	}
}
