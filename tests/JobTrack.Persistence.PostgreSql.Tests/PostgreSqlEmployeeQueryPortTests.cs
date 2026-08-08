namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using Application.Ports;
using Database;
using NodaTime;
using Npgsql;
using Persistence.Shared.Ports;
using TestSupport;

public sealed class PostgreSqlEmployeeQueryPortTests()
	: EmployeeQueryPortContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	internal override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new PostgreSqlInstallationBootstrapPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	internal override IEmployeeQueryPort CreateQueryPort(string connectionString) =>
		new EmployeeQueryPort(new PostgreSqlReadOperations(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build()), SystemClock.Instance);

	internal override IEmployeeCommandPort CreateCommandPort(string connectionString) =>
		new EmployeeCommandPort(new PostgreSqlWriteOperations(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build()), SystemClock.Instance);
}
