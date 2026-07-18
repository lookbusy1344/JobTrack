namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using TestSupport;

/// <summary>
///     SQLite-only: every table is declared <c>STRICT</c>, and the pure
///     junction/edge tables plus <c>leaf_work</c>'s 1:1 extension of
///     <c>job_node</c> are declared <c>WITHOUT ROWID</c> (no PostgreSQL
///     equivalent for either concept).
/// </summary>
public sealed class SqliteStrictAndWithoutRowidSchemaTests : IAsyncLifetime
{
	private static readonly IReadOnlyList<string> AllTableNames = [
		"schema_version", "achievement_status",
		"app_user", "identity_user", "identity_role", "identity_user_role",
		"initialised_marker",
		"priority", "job_node",
		"leaf_work",
		"work_session",
		"job_prerequisite",
		"user_schedule_version", "user_schedule_interval",
		"schedule_exception_effect", "user_schedule_exception",
		"user_cost_rate", "node_rate_override",
		"audit_event",
		"personal_access_token",
		"department", "app_user_department",
		"request_holding_area", "job_request", "job_request_note",
	];

	private static readonly IReadOnlyList<string> WithoutRowidTableNames = [
		"identity_user_role", "job_prerequisite", "leaf_work",
		"app_user_department", "job_request",
	];

	private readonly SqliteDatabaseFixture database = new();

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Every_table_is_declared_STRICT()
	{
		await using var connection = await DeployedConnectionAsync();

		foreach (var tableName in AllTableNames) {
			(await ReadTablePragmaFlagAsync(connection, tableName, "strict"))
				.Should().Be(1, $"{tableName} should be STRICT");
		}
	}

	[Fact]
	public async Task Pure_junction_and_leaf_work_extension_tables_are_WITHOUT_ROWID()
	{
		await using var connection = await DeployedConnectionAsync();

		foreach (var tableName in WithoutRowidTableNames) {
			(await ReadTablePragmaFlagAsync(connection, tableName, "wr"))
				.Should().Be(1, $"{tableName} should be WITHOUT ROWID");
		}
	}

	[Fact]
	public async Task Surrogate_keyed_tables_retain_a_rowid()
	{
		await using var connection = await DeployedConnectionAsync();

		foreach (var tableName in AllTableNames.Except(WithoutRowidTableNames)) {
			(await ReadTablePragmaFlagAsync(connection, tableName, "wr"))
				.Should().Be(0, $"{tableName} should keep its rowid");
		}
	}

	[Fact]
	public async Task Deploying_a_fresh_SQLite_database_enables_WAL_journal_mode()
	{
		var options = new DeployCommandOptions {
			Provider = SchemaProvider.Sqlite,
			ConnectionString = database.ConnectionString,
			ScriptsRoot = RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.Sqlite),
		};

		await Program.DeployAsync(options, CancellationToken.None);

		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "PRAGMA journal_mode;";
		var journalMode = (string?)await command.ExecuteScalarAsync();

		journalMode.Should().Be("wal");
	}

	private async Task<DbConnection> DeployedConnectionAsync()
	{
		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.Sqlite));

		var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		var deployer = new SchemaDeployer(
			connection, new SqliteSchemaVersionStore(), new SqliteDeploymentLockStrategy(), "1.0.0", "test-runner");
		await deployer.DeployAsync(scripts, CancellationToken.None);

		return connection;
	}

	private static async Task<long> ReadTablePragmaFlagAsync(DbConnection connection, string tableName, string columnName)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT {columnName} FROM pragma_table_list($name) WHERE type = 'table';";

		var parameter = command.CreateParameter();
		parameter.ParameterName = "$name";
		parameter.Value = tableName;
		command.Parameters.Add(parameter);

		var result = await command.ExecuteScalarAsync();
		return result is null
			? throw new InvalidOperationException($"Table '{tableName}' was not found by pragma_table_list.")
			: (long)result;
	}
}
