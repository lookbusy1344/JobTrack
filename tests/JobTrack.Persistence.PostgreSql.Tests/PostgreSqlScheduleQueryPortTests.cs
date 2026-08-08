namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using Application.Ports;
using Database;
using NodaTime;
using Npgsql;
using Persistence.Shared.Ports;
using TestSupport;

public sealed class PostgreSqlScheduleQueryPortTests()
	: ScheduleQueryPortContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	internal override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new PostgreSqlInstallationBootstrapPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	internal override IScheduleCommandPort CreateCommandPort(string connectionString) =>
		new ScheduleCommandPort(new PostgreSqlWriteOperations(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build()), SystemClock.Instance);

	internal override IScheduleQueryPort CreateQueryPort(string connectionString) =>
		new ScheduleQueryPort(new PostgreSqlReadOperations(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build()), SystemClock.Instance);
}
