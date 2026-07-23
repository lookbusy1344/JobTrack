namespace JobTrack.Persistence.Sqlite.Tests;

using System.Data.Common;
using Application.Ports;
using Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using TestSupport;

public sealed class SqliteCostQueryPortTests()
	: CostQueryPortContractTestsBase(new SqliteDatabaseFixture())
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

	internal override IJobNodeCommandPort CreateJobNodePort(string connectionString) =>
		new SqliteJobNodeCommandPort(connectionString, SystemClock.Instance);

	internal override IScheduleCommandPort CreateSchedulePort(string connectionString) =>
		new SqliteScheduleCommandPort(connectionString, SystemClock.Instance);

	internal override IRateCommandPort CreateRatePort(string connectionString) =>
		new SqliteRateCommandPort(connectionString, SystemClock.Instance);

	internal override IWorkSessionCommandPort CreateSessionPort(string connectionString) =>
		new SqliteWorkSessionCommandPort(connectionString, SystemClock.Instance);

	internal override ICostQueryPort CreateCostQueryPort(string connectionString) =>
		new SqliteCostQueryPort(connectionString, SystemClock.Instance);

	internal override ICostQueryPort CreateCostQueryPortWithInterceptors(
		string connectionString, IReadOnlyList<IInterceptor> interceptors) =>
		new SqliteCostQueryPort(connectionString, SystemClock.Instance, interceptors);
}
