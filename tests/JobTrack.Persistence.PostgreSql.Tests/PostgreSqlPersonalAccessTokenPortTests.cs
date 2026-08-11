namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using Application.Ports;
using Database;
using NodaTime;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlPersonalAccessTokenPortTests()
	: PersonalAccessTokenPortContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	internal override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new PostgreSqlInstallationBootstrapPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	internal override IPersonalAccessTokenPort CreatePort(string connectionString) =>
		CreatePort(connectionString, SystemClock.Instance);

	internal override IPersonalAccessTokenPort CreatePort(string connectionString, IClock clock) =>
		new PostgreSqlPersonalAccessTokenPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), clock);

	protected override object FormatInstantForRawSql(Instant instant) => instant.ToDateTimeOffset();
}
