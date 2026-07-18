namespace JobTrack.Persistence.PostgreSql;

using Abstractions;
using Application;
using Application.Ports;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shared.Entities;

/// <summary>
///     PostgreSQL implementation of <see cref="ILeafWorkQueryPort" /> (plan §8.5 slice 5). One
///     <see cref="PostgreSqlJobTrackDbContext" /> per call, read-only throughout.
/// </summary>
internal sealed class PostgreSqlLeafWorkQueryPort : ILeafWorkQueryPort
{
	private readonly NpgsqlDataSource dataSource;

	/// <summary>Creates the port over the given pooled <see cref="NpgsqlDataSource" />.</summary>
	public PostgreSqlLeafWorkQueryPort(NpgsqlDataSource dataSource) => this.dataSource = dataSource;

	/// <inheritdoc />
	public async Task<LeafWorkResult> GetLeafWorkAsync(JobNodeId jobNodeId, CancellationToken cancellationToken = default)
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, o => o.UseNodaTime())
			.Options;
		await using var context = new PostgreSqlJobTrackDbContext(options);

		var leafWork = await context.Set<LeafWorkEntity>().AsNoTracking()
						   .FirstOrDefaultAsync(lw => lw.JobNodeId == jobNodeId, cancellationToken).ConfigureAwait(false)
					   ?? throw new EntityNotFoundException($"Job node {jobNodeId} has no LeafWork attached.");

		return new() {
			JobNodeId = leafWork.JobNodeId,
			Achievement = leafWork.Achievement,
			PartialCriteria = leafWork.PartialCriteria,
			FullCriteria = leafWork.FullCriteria,
			ChangedAt = leafWork.ChangedAt,
			Version = leafWork.RowVersion,
		};
	}
}
