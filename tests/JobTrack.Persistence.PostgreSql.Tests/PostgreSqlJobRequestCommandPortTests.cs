namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using Application.Ports;
using Database;
using NodaTime;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlJobRequestCommandPortTests()
	: JobRequestCommandPortContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new PostgreSqlInstallationBootstrapPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	protected override IJobRequestCommandPort CreateCommandPort(string connectionString) =>
		new PostgreSqlJobRequestCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	protected override IAuditQueryPort CreateAuditQueryPort(string connectionString) =>
		new PostgreSqlAuditQueryPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	protected override IJobBrowseQueryPort CreateBrowsePort(string connectionString) =>
		new PostgreSqlJobBrowseQueryPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());

	protected override IJobNodeCommandPort CreateJobNodeCommandPort(string connectionString) =>
		new PostgreSqlJobNodeCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	protected override object EncodeInstant(DateTimeOffset value) => value;
}
