namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using Application.Ports;
using Database;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using Npgsql;
using Persistence.Shared.Ports;
using TestSupport;

public sealed class PostgreSqlAwaitingProgressQueryPortTests()
	: AwaitingProgressQueryPortContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	internal override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new PostgreSqlInstallationBootstrapPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	internal override IJobNodeCommandPort CreateJobNodePort(string connectionString) =>
		new PostgreSqlJobNodeCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	internal override IAchievementCommandPort CreateAchievementPort(string connectionString) =>
		new PostgreSqlAchievementCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	internal override IWorkSessionCommandPort CreateSessionPort(string connectionString) =>
		new WorkSessionCommandPort(new PostgreSqlWriteOperations(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build()), SystemClock.Instance);

	internal override IAwaitingProgressQueryPort CreatePort(string connectionString) =>
		new PostgreSqlAwaitingProgressQueryPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());

	internal override IAwaitingProgressQueryPort CreatePort(string connectionString, IReadOnlyList<IInterceptor> interceptors) =>
		new PostgreSqlAwaitingProgressQueryPort(
			new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), interceptors);
}
