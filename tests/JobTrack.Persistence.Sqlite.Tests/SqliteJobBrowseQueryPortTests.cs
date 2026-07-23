namespace JobTrack.Persistence.Sqlite.Tests;

using System.Data.Common;
using Application.Ports;
using Database;
using Microsoft.Data.Sqlite;
using NodaTime;
using TestSupport;

public sealed class SqliteJobBrowseQueryPortTests()
	: JobBrowseQueryPortContractTestsBase(new SqliteDatabaseFixture())
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

	internal override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new SqliteInstallationBootstrapPort(connectionString, SystemClock.Instance);

	internal override IJobNodeCommandPort CreateCommandPort(string connectionString) =>
		new SqliteJobNodeCommandPort(connectionString, SystemClock.Instance);

	internal override IAchievementCommandPort CreateAchievementPort(string connectionString) =>
		new SqliteAchievementCommandPort(connectionString, SystemClock.Instance);

	internal override IJobBrowseQueryPort CreateBrowsePort(string connectionString) =>
		new SqliteJobBrowseQueryPort(connectionString);

	internal override IJobBrowseQueryPort CreateBrowsePortWithCommandCounter(string connectionString, CommandCountInterceptor interceptor) =>
		new SqliteJobBrowseQueryPort(connectionString, [interceptor]);
}
