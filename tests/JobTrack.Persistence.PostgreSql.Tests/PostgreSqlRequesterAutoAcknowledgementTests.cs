namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using Application.Ports;
using Database;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

	[Fact]
	public Task Concurrent_first_work_on_two_leaves_under_one_request_acknowledges_it_exactly_once() =>
		AssertConcurrentFirstWorkAcknowledgesExactlyOnceAsync();

	[Fact]
	public Task Concurrent_terminal_outcomes_on_two_leaves_under_one_request_acknowledge_it_exactly_once() =>
		AssertConcurrentTerminalOutcomeAcknowledgesExactlyOnceAsync();

	[Fact]
	public Task Concurrent_terminal_outcomes_reach_the_request_update_before_either_proceeds() =>
		AssertDeterministicConcurrentTerminalOutcomeAcknowledgesExactlyOnceAsync(CreateAchievementPort);

	private IAchievementCommandPort CreateAchievementPort(DbCommandInterceptor interceptor) =>
		new PostgreSqlAchievementCommandPort(
			new NpgsqlDataSourceBuilder(ConnectionString).UseNodaTime().Build(), SystemClock.Instance, [interceptor]);
}
