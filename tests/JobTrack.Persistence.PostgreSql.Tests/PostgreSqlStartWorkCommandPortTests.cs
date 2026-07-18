namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using Application.Ports;
using Database;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlStartWorkCommandPortTests()
	: StartWorkCommandPortContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new PostgreSqlInstallationBootstrapPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());

	protected override IJobNodeCommandPort CreateJobNodePort(string connectionString) =>
		new PostgreSqlJobNodeCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());

	protected override IWorkSessionCommandPort CreateSessionPort(string connectionString) =>
		new PostgreSqlWorkSessionCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());

	protected override ILeafWorkQueryPort CreateLeafWorkQueryPort(string connectionString) =>
		new PostgreSqlLeafWorkQueryPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());

	protected override IAuditQueryPort CreateAuditQueryPort(string connectionString) =>
		new PostgreSqlAuditQueryPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());
}
