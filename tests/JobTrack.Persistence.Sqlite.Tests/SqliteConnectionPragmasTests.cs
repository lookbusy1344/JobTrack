namespace JobTrack.Persistence.Sqlite.Tests;

using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using TestSupport;

/// <summary>
///     Every write-side command port issues <see cref="SqliteConnectionPragmas.ConfigureConnectionSql" />
///     immediately after opening its connection; this asserts the resulting
///     connection state directly, independent of any one port. A file-backed
///     database is used (not <c>:memory:</c>) because WAL is a no-op on
///     in-memory/temporary databases.
/// </summary>
public sealed class SqliteConnectionPragmasTests : IAsyncLifetime
{
	private readonly SqliteDatabaseFixture database = new();

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task ConfigureConnectionSql_sets_WAL_journal_mode_and_NORMAL_synchronous()
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using (var configureCommand = connection.CreateCommand()) {
			configureCommand.CommandText = SqliteConnectionPragmas.ConfigureConnectionSql;
			_ = await configureCommand.ExecuteNonQueryAsync();
		}

		(await ReadPragmaAsync(connection, "journal_mode")).Should().Be("wal");
		(await ReadPragmaAsync(connection, "synchronous")).Should().Be(1L);
		(await ReadPragmaAsync(connection, "foreign_keys")).Should().Be(1L);
		(await ReadPragmaAsync(connection, "busy_timeout")).Should().Be(5000L);
	}

	private static async Task<object> ReadPragmaAsync(SqliteConnection connection, string pragmaName)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = $"PRAGMA {pragmaName};";
		return await command.ExecuteScalarAsync() ?? throw new InvalidOperationException($"PRAGMA {pragmaName} returned no value.");
	}
}
