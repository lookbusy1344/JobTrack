namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using Application.Ports;
using Database;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlWorkSessionQueryPortTests()
	: WorkSessionQueryPortContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new PostgreSqlInstallationBootstrapPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());

	protected override IJobNodeCommandPort CreateJobCommandPort(string connectionString) =>
		new PostgreSqlJobNodeCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());

	protected override IWorkSessionCommandPort CreateSessionCommandPort(string connectionString) =>
		new PostgreSqlWorkSessionCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());

	protected override IWorkSessionQueryPort CreateQueryPort(string connectionString) =>
		new PostgreSqlWorkSessionQueryPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());
}
