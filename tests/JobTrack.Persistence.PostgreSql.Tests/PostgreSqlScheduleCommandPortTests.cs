namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using Application.Ports;
using Database;
using NodaTime;
using Npgsql;
using Shared.Ports;
using TestSupport;

public sealed class PostgreSqlScheduleCommandPortTests()
	: ScheduleCommandPortContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	internal override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new PostgreSqlInstallationBootstrapPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	internal override IScheduleCommandPort CreateSchedulePort(string connectionString) =>
		new ScheduleCommandPort(new PostgreSqlWriteOperations(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build()),
			SystemClock.Instance);

	internal override IAuditQueryPort CreateAuditQueryPort(string connectionString) =>
		new AuditQueryPort(new PostgreSqlReadOperations(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build()), SystemClock.Instance);
}
