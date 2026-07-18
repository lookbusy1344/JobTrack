namespace JobTrack.Database;

using System.Data.Common;
using Npgsql;
using NpgsqlTypes;

/// <inheritdoc cref="ISchemaVersionStore" />
public sealed class PostgreSqlSchemaVersionStore : ISchemaVersionStore
{
	public async Task<IReadOnlyList<AppliedSchemaVersion>> GetAppliedVersionsAsync(
		DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
	{
		var npgsqlConnection = (NpgsqlConnection)connection;
		var npgsqlTransaction = (NpgsqlTransaction)transaction;

		if (!await TrackingTableExistsAsync(npgsqlConnection, npgsqlTransaction, cancellationToken).ConfigureAwait(false)) {
			return [];
		}

		var appliedVersions = new List<AppliedSchemaVersion>();

		await using var command = npgsqlConnection.CreateCommand();
		command.Transaction = npgsqlTransaction;
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
				AppliedAtUtc = reader.GetFieldValue<DateTimeOffset>(5),
			});
		}

		return appliedVersions;
	}

	public async Task RecordAppliedVersionAsync(
		DbConnection connection, DbTransaction transaction, AppliedSchemaVersion appliedVersion, CancellationToken cancellationToken)
	{
		var npgsqlConnection = (NpgsqlConnection)connection;

		await using var command = npgsqlConnection.CreateCommand();
		command.Transaction = (NpgsqlTransaction)transaction;
		command.CommandText =
			"INSERT INTO schema_version (version, description, checksum, applied_by, application_version, applied_at) " +
			"VALUES (@version, @description, @checksum, @appliedBy, @applicationVersion, @appliedAt);";
		_ = command.Parameters.AddWithValue("version", appliedVersion.Version);
		_ = command.Parameters.AddWithValue("description", appliedVersion.Description);
		_ = command.Parameters.AddWithValue("checksum", appliedVersion.Checksum);
		_ = command.Parameters.AddWithValue("appliedBy", appliedVersion.AppliedBy);
		_ = command.Parameters.AddWithValue("applicationVersion", appliedVersion.ApplicationVersion);
		_ = command.Parameters.Add(new("appliedAt", NpgsqlDbType.TimestampTz) { Value = appliedVersion.AppliedAtUtc });

		_ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task<bool> TrackingTableExistsAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
	{
		await using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "SELECT to_regclass('public.schema_version') IS NOT NULL;";

		return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
	}
}
