namespace JobTrack.Database;

using System.Data.Common;
using Microsoft.Data.Sqlite;

/// <inheritdoc cref="ISchemaVersionStore" />
public sealed class SqliteSchemaVersionStore : ISchemaVersionStore
{
	public async Task<IReadOnlyList<AppliedSchemaVersion>> GetAppliedVersionsAsync(
		DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
	{
		var sqliteConnection = (SqliteConnection)connection;
		var sqliteTransaction = (SqliteTransaction)transaction;

		if (!await TrackingTableExistsAsync(sqliteConnection, sqliteTransaction, cancellationToken).ConfigureAwait(false)) {
			return [];
		}

		var appliedVersions = new List<AppliedSchemaVersion>();

		await using var command = sqliteConnection.CreateCommand();
		command.Transaction = sqliteTransaction;
		command.CommandText =
			"SELECT version, description, checksum, applied_by, application_version, applied_at " +
			"FROM schema_version ORDER BY version;";

		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
			appliedVersions.Add(new() {
				Version = reader.GetInt32(0),
				Description = reader.GetString(1),
				Checksum = reader.GetString(2),
				AppliedBy = reader.GetString(3),
				ApplicationVersion = reader.GetString(4),
				AppliedAtUtc = SqliteInstantEncoding.ToDateTimeOffset(reader.GetInt64(5)),
			});
		}

		return appliedVersions;
	}

	public async Task RecordAppliedVersionAsync(
		DbConnection connection, DbTransaction transaction, AppliedSchemaVersion appliedVersion, CancellationToken cancellationToken)
	{
		var sqliteConnection = (SqliteConnection)connection;

		await using var command = sqliteConnection.CreateCommand();
		command.Transaction = (SqliteTransaction)transaction;
		command.CommandText =
			"INSERT INTO schema_version (version, description, checksum, applied_by, application_version, applied_at) " +
			"VALUES ($version, $description, $checksum, $appliedBy, $applicationVersion, $appliedAt);";
		_ = command.Parameters.AddWithValue("$version", appliedVersion.Version);
		_ = command.Parameters.AddWithValue("$description", appliedVersion.Description);
		_ = command.Parameters.AddWithValue("$checksum", appliedVersion.Checksum);
		_ = command.Parameters.AddWithValue("$appliedBy", appliedVersion.AppliedBy);
		_ = command.Parameters.AddWithValue("$applicationVersion", appliedVersion.ApplicationVersion);
		_ = command.Parameters.AddWithValue("$appliedAt", SqliteInstantEncoding.ToUnixEpochTicks(appliedVersion.AppliedAtUtc));

		_ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task<bool> TrackingTableExistsAsync(
		SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
	{
		await using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_version';";

		var tableCount = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
		return tableCount > 0;
	}
}
