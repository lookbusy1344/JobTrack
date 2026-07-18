namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using Application.Ports;
using Database;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlJobBrowseQueryPortTests()
	: JobBrowseQueryPortContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new PostgreSqlInstallationBootstrapPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());

	protected override IJobNodeCommandPort CreateCommandPort(string connectionString) =>
		new PostgreSqlJobNodeCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());

	protected override IJobBrowseQueryPort CreateBrowsePort(string connectionString) =>
		new PostgreSqlJobBrowseQueryPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());

	protected override IJobBrowseQueryPort CreateBrowsePortWithCommandCounter(string connectionString, CommandCountInterceptor interceptor) =>
		new PostgreSqlJobBrowseQueryPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), [interceptor]);
}
