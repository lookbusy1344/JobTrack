namespace JobTrack.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>
///     Creates a uniquely named, disposable file-backed SQLite database for one
///     test class, and deletes the file on disposal (§6.6).
/// </summary>
public sealed class SqliteDatabaseFixture : IDisposableTestDatabase
{
	private const string WalSidecarSuffix = "-wal";
	private const string SharedMemorySidecarSuffix = "-shm";
	private const string RollbackJournalSidecarSuffix = "-journal";

	private readonly string databaseFilePath = Path.Combine(Path.GetTempPath(), $"jobtrack_test_{Guid.NewGuid():N}.db");

	public string ConnectionString { get; private set; } = string.Empty;

	public Task InitializeAsync()
	{
		ConnectionString = new SqliteConnectionStringBuilder { DataSource = databaseFilePath }.ConnectionString;
		return Task.CompletedTask;
	}

	public Task DisposeAsync()
	{
		// Microsoft.Data.Sqlite pools connections by default, so a caller's
		// SqliteConnection.Dispose() doesn't release the underlying file
		// handle -- without clearing the pool first, WAL mode's -wal/-shm
		// sidecars survive deleting the main file.
		using (var connection = new SqliteConnection(ConnectionString)) {
			SqliteConnection.ClearPool(connection);
		}

		DeleteIfExists(databaseFilePath);
		DeleteIfExists(databaseFilePath + WalSidecarSuffix);
		DeleteIfExists(databaseFilePath + SharedMemorySidecarSuffix);
		DeleteIfExists(databaseFilePath + RollbackJournalSidecarSuffix);

		return Task.CompletedTask;
	}

	private static void DeleteIfExists(string path)
	{
		if (File.Exists(path)) {
			File.Delete(path);
		}
	}
}
