namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using Application.Ports;
using Database;
using NodaTime;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlRateQueryPortTests()
	: RateQueryPortContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new PostgreSqlInstallationBootstrapPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	protected override IRateCommandPort CreateCommandPort(string connectionString) =>
		new PostgreSqlRateCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	protected override IRateQueryPort CreateQueryPort(string connectionString) =>
		new PostgreSqlRateQueryPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);
}
