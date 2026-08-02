namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using Application.Ports;
using Database;
using NodaTime;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlRequesterAutoAcknowledgementTests()
	: RequesterAutoAcknowledgementContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	internal override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new PostgreSqlInstallationBootstrapPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	internal override IJobRequestCommandPort CreateRequestPort(string connectionString) =>
		new PostgreSqlJobRequestCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	internal override IJobNodeCommandPort CreateJobNodePort(string connectionString) =>
		new PostgreSqlJobNodeCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	internal override IWorkSessionCommandPort CreateSessionPort(string connectionString) =>
		new PostgreSqlWorkSessionCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	internal override IAchievementCommandPort CreateAchievementPort(string connectionString) =>
		new PostgreSqlAchievementCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	internal override IAuditQueryPort CreateAuditQueryPort(string connectionString) =>
		new PostgreSqlAuditQueryPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	protected override object EncodeInstant(DateTimeOffset value) => value;
}
