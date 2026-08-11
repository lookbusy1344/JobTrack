namespace JobTrack.Database.ContractTests;

using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using TestSupport;

public sealed class SqliteDatabaseFixtureTests
{
	[Fact]
	public async Task Fixture_disposal_removes_WAL_and_shared_memory_sidecar_files()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		var databaseFilePath = new SqliteConnectionStringBuilder(database.ConnectionString).DataSource;
		var walPath = $"{databaseFilePath}-wal";
		var sharedMemoryPath = $"{databaseFilePath}-shm";

		await using (var connection = new SqliteConnection(database.ConnectionString)) {
			await connection.OpenAsync();
			await using var command = connection.CreateCommand();
			command.CommandText = "PRAGMA journal_mode = WAL; CREATE TABLE probe (id INTEGER PRIMARY KEY); INSERT INTO probe (id) VALUES (1);";
			_ = await command.ExecuteNonQueryAsync();
		}

		// Microsoft.Data.Sqlite pools connections by default, so disposing the
		// connection above does not release its file handle -- the -wal
		// sidecar is still present, which is exactly the state a real
		// application connection leaves behind.
		File.Exists(walPath).Should().BeTrue();

		await database.DisposeAsync();

		File.Exists(databaseFilePath).Should().BeFalse();
		File.Exists(walPath).Should().BeFalse();
		File.Exists(sharedMemoryPath).Should().BeFalse();
	}
}
