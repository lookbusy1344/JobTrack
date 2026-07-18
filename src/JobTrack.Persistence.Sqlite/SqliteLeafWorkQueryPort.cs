namespace JobTrack.Persistence.Sqlite;

using Abstractions;
using Application;
using Application.Ports;
using Microsoft.EntityFrameworkCore;
using Shared.Entities;

/// <summary>
///     SQLite implementation of <see cref="ILeafWorkQueryPort" /> (plan §8.5 slice 5). One
///     <see cref="SqliteJobTrackDbContext" /> per call, read-only throughout.
/// </summary>
internal sealed class SqliteLeafWorkQueryPort : ILeafWorkQueryPort
{
	private readonly string connectionString;

	/// <summary>Creates the port over the given SQLite connection string.</summary>
	public SqliteLeafWorkQueryPort(string connectionString) => this.connectionString = connectionString;

	/// <inheritdoc />
	public async Task<LeafWorkResult> GetLeafWorkAsync(JobNodeId jobNodeId, CancellationToken cancellationToken = default)
	{
		await using var context = SqliteDbContextFactory.CreateContext(connectionString);

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
