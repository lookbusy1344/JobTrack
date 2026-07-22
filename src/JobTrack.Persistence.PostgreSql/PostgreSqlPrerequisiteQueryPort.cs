namespace JobTrack.Persistence.PostgreSql;

using Abstractions;
using Application.Ports;
using Domain.Hierarchy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shared.Entities;

/// <summary>
///     PostgreSQL implementation of <see cref="IPrerequisiteQueryPort" /> (plan §8.5 slice 5). One
///     <see cref="PostgreSqlJobTrackDbContext" /> per call, read-only throughout.
/// </summary>
internal sealed class PostgreSqlPrerequisiteQueryPort : IPrerequisiteQueryPort
{
	private readonly NpgsqlDataSource dataSource;

	/// <summary>Creates the port over the given pooled <see cref="NpgsqlDataSource" />.</summary>
	public PostgreSqlPrerequisiteQueryPort(NpgsqlDataSource dataSource) => this.dataSource = dataSource;

	/// <inheritdoc />
	public async Task<int> CountDirectDependentsAsync(JobNodeId requiredJobId, CancellationToken cancellationToken = default)
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, o => o.UseNodaTime())
			.Options;
		await using var context = new PostgreSqlJobTrackDbContext(options);

		if (!await context.Set<JobNodeEntity>().AsNoTracking()
				.AnyAsync(n => n.Id == requiredJobId, cancellationToken).ConfigureAwait(false)) {
			throw new EntityNotFoundException($"Job node {requiredJobId} does not exist.");
		}

		return await context.Set<JobPrerequisiteEntity>().AsNoTracking()
			.CountAsync(edge => edge.FromId == requiredJobId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task<EquatableArray<PrerequisiteEdge>> GetPrerequisitesAsync(
		JobNodeId nodeId, int offset = 0, int? limit = null, CancellationToken cancellationToken = default)
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, o => o.UseNodaTime())
			.Options;
		await using var context = new PostgreSqlJobTrackDbContext(options);

		if (!await context.Set<JobNodeEntity>().AsNoTracking()
				.AnyAsync(n => n.Id == nodeId, cancellationToken).ConfigureAwait(false)) {
			throw new EntityNotFoundException($"Job node {nodeId} does not exist.");
		}

		var query = context.Set<JobPrerequisiteEntity>().AsNoTracking()
			.Where(jp => jp.FromId == nodeId || jp.ToId == nodeId)
			.OrderBy(jp => jp.FromId).ThenBy(jp => jp.ToId)
			.Skip(offset)
			.Select(jp => new PrerequisiteEdge(jp.FromId, jp.ToId));
		var edges = await (limit.HasValue ? query.Take(limit.Value) : query)
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);

		return [.. edges];
	}
}
