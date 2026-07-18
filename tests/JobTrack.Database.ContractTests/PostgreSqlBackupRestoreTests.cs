namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using Npgsql;
using TestSupport;

/// <summary>
///     TC-DB-BACKUP-001: §6.7 gate item "a schema-level PostgreSQL
///     backup/restore smoke test passes" (docs/operations/postgresql-backup-restore.md).
///     Dumps a fully deployed database with <c>pg_dump</c> and restores it into
///     a second, empty disposable database with <c>pg_restore</c>, then checks
///     the restored database has the same schema-version history, reference
///     data, a seeded row, and enforced role grants as the source.
///     Roles themselves are cluster-scoped (<c>CREATE ROLE</c> is not
///     per-database, so a plain <c>pg_dump</c> of one database does not carry
///     them) and already exist on this instance from
///     <see cref="PostgreSqlRolesAndGrants.ApplyAsync" /> having been applied to
///     the source database, the same as every other fixture-backed test in this
///     project. This test therefore proves that the GRANT/REVOKE statements
///     captured in the dump still resolve correctly against those pre-existing
///     roles once restored -- not that role provisioning itself round-trips.
/// </summary>
public sealed class PostgreSqlBackupRestoreTests : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private readonly string dumpFilePath = Path.Combine(Path.GetTempPath(), $"jobtrack_backup_{Guid.NewGuid():N}.dump");

	private readonly PostgreSqlDatabaseFixture source = new();
	private readonly PostgreSqlDatabaseFixture target = new();

	public async Task InitializeAsync()
	{
		await source.InitializeAsync();
		await target.InitializeAsync();
	}

	public async Task DisposeAsync()
	{
		await source.DisposeAsync();
		await target.DisposeAsync();

		if (File.Exists(dumpFilePath)) {
			File.Delete(dumpFilePath);
		}
	}

	[Fact]
	public async Task A_deployed_database_survives_a_pg_dump_and_pg_restore_round_trip()
	{
		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.PostgreSql));
		long seededAppUserId;

		await using (var sourceConnection = new NpgsqlConnection(source.ConnectionString)) {
			await sourceConnection.OpenAsync();

			var deployer = new SchemaDeployer(
				sourceConnection, new PostgreSqlSchemaVersionStore(), new PostgreSqlDeploymentLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);
			await PostgreSqlRolesAndGrants.ApplyAsync(
				sourceConnection, RepositoryPaths.PostgreSqlRolesAndGrantsScriptPath(), CancellationToken.None);

			seededAppUserId = await SeedAppUserAsync(sourceConnection, "Alice Example");
		}

		await PostgreSqlCliTool.DumpAsync(source.ConnectionString, dumpFilePath, CancellationToken.None);
		await PostgreSqlCliTool.RestoreAsync(target.ConnectionString, dumpFilePath, CancellationToken.None);

		await using var targetConnection = new NpgsqlConnection(target.ConnectionString);
		await targetConnection.OpenAsync();

		(await ReadAppliedVersionCountAsync(targetConnection)).Should().Be(scripts.Count);
		(await ReadNamesAsync(targetConnection, "achievement_status")).Should()
			.BeEquivalentTo("Waiting", "InProgress", "Success", "Cancelled", "Unsuccessful");
		(await ReadDisplayNameAsync(targetConnection, seededAppUserId)).Should().Be("Alice Example");

		var act = async () => await ExecuteAsRoleAsync(targetConnection, "jobtrack_application", "CREATE TABLE rogue_table (id integer);");
		await act.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
	}

	private static async Task ExecuteAsRoleAsync(NpgsqlConnection connection, string role, string commandText)
	{
		await using var setRole = connection.CreateCommand();
		setRole.CommandText = $"SET ROLE {role};";
		_ = await setRole.ExecuteNonQueryAsync();

		try {
			await using var command = connection.CreateCommand();
			command.CommandText = commandText;
			_ = await command.ExecuteNonQueryAsync();
		}
		finally {
			await using var resetRole = connection.CreateCommand();
			resetRole.CommandText = "RESET ROLE;";
			_ = await resetRole.ExecuteNonQueryAsync();
		}
	}

	private static async Task<long> SeedAppUserAsync(DbConnection connection, string displayName)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO app_user (display_name, iana_time_zone)
							  VALUES (@displayName, 'Europe/London')
							  RETURNING id;
							  """;
		AddParameter(command, "@displayName", displayName);

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static async Task<string> ReadDisplayNameAsync(DbConnection connection, long appUserId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT display_name FROM app_user WHERE id = @id;";
		AddParameter(command, "@id", appUserId);

		return (string)(await command.ExecuteScalarAsync())!;
	}

	private static async Task<long> ReadAppliedVersionCountAsync(DbConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM schema_version;";
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static async Task<IReadOnlyList<string>> ReadNamesAsync(DbConnection connection, string tableName)
	{
		var names = new List<string>();

		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT name FROM {tableName} ORDER BY id;";

		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			names.Add(reader.GetString(0));
		}

		return names;
	}

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}
