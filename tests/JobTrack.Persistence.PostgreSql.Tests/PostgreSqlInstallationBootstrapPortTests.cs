namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using Application.Ports;
using Database;
using NodaTime;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlInstallationBootstrapPortTests()
	: InstallationBootstrapPortContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override IInstallationBootstrapPort CreatePort(string connectionString)
	{
		var dataSource = new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build();
		return new PostgreSqlInstallationBootstrapPort(dataSource, SystemClock.Instance);
	}
}
