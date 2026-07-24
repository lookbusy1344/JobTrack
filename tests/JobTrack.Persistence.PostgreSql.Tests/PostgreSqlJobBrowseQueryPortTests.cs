namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using Application.Ports;
using Database;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
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

	internal override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new PostgreSqlInstallationBootstrapPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	internal override IJobNodeCommandPort CreateCommandPort(string connectionString) =>
		new PostgreSqlJobNodeCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	internal override IAchievementCommandPort CreateAchievementPort(string connectionString) =>
		new PostgreSqlAchievementCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	internal override IJobBrowseQueryPort CreateBrowsePort(string connectionString) =>
		new PostgreSqlJobBrowseQueryPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());

	internal override IJobBrowseQueryPort CreateBrowsePortWithInterceptor(string connectionString, DbCommandInterceptor interceptor) =>
		new PostgreSqlJobBrowseQueryPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), [interceptor]);
}
