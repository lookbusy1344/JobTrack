namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using Application.Ports;
using Database;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlEmployeeQueryPortTests()
	: EmployeeQueryPortContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new PostgreSqlInstallationBootstrapPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());

	protected override IEmployeeQueryPort CreateQueryPort(string connectionString) =>
		new PostgreSqlEmployeeQueryPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());

	protected override IEmployeeCommandPort CreateCommandPort(string connectionString) =>
		new PostgreSqlEmployeeCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());
}
