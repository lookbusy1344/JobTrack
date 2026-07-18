namespace JobTrack.Persistence.Sqlite.Tests;

using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class SqliteCostQuerySnapshotTests
{
	[Fact]
	public async Task Cost_query_snapshot_is_deferred_until_the_first_read()
	{
		var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.GetTempFileName() }.ConnectionString;
		await using var firstConnection = new SqliteConnection(connectionString);
		await firstConnection.OpenAsync();
		await using var setup = firstConnection.CreateCommand();
		setup.CommandText = "CREATE TABLE snapshot_probe (value INTEGER NOT NULL);";
		_ = await setup.ExecuteNonQueryAsync();

		var options = new DbContextOptionsBuilder<SqliteJobTrackDbContext>().UseSqlite(firstConnection).Options;
		await using var context = new SqliteJobTrackDbContext(options);
		await using var transaction = await SqliteCostQuerySnapshot.BeginAsync(context, CancellationToken.None);

		await using var secondConnection = new SqliteConnection(connectionString);
		await secondConnection.OpenAsync();
		await using var insert = secondConnection.CreateCommand();
		insert.CommandText = "INSERT INTO snapshot_probe (value) VALUES (1);";
		var affected = await insert.ExecuteNonQueryAsync();

		affected.Should().Be(1);
	}
}
