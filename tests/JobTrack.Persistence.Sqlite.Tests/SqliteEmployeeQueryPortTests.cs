namespace JobTrack.Persistence.Sqlite.Tests;

using System.Data.Common;
using Application.Ports;
using Database;
using Microsoft.Data.Sqlite;
using NodaTime;
using TestSupport;

public sealed class SqliteEmployeeQueryPortTests()
	: EmployeeQueryPortContractTestsBase(new SqliteDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.Sqlite;

	protected override DbConnection CreateConnection(string connectionString) => new SqliteConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new SqliteSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new SqliteDeploymentLockStrategy();

	protected override async Task PrepareConnectionAsync(DbConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
		_ = await command.ExecuteNonQueryAsync();
	}

	protected override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new SqliteInstallationBootstrapPort(connectionString, SystemClock.Instance);

	protected override IEmployeeQueryPort CreateQueryPort(string connectionString) =>
		new SqliteEmployeeQueryPort(connectionString, SystemClock.Instance);

	protected override IEmployeeCommandPort CreateCommandPort(string connectionString) =>
		new SqliteEmployeeCommandPort(connectionString, SystemClock.Instance);
}
