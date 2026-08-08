namespace JobTrack.Persistence.Sqlite.Tests;

using System.Data.Common;
using Application.Ports;
using Database;
using Microsoft.Data.Sqlite;
using NodaTime;
using Persistence.Shared.Ports;
using TestSupport;

public sealed class SqliteRequesterAutoAcknowledgementTests()
	: RequesterAutoAcknowledgementContractTestsBase(new SqliteDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.Sqlite;

	protected override DbConnection CreateConnection(string connectionString) => new SqliteConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new SqliteSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new SqliteDeploymentLockStrategy();

	protected override async Task PrepareConnectionAsync(DbConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
		_ = await command.ExecuteNonQueryAsync();
	}

	internal override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new SqliteInstallationBootstrapPort(connectionString, SystemClock.Instance);

	internal override IJobRequestCommandPort CreateRequestPort(string connectionString) =>
		new SqliteJobRequestCommandPort(connectionString, SystemClock.Instance);

	internal override IJobNodeCommandPort CreateJobNodePort(string connectionString) =>
		new SqliteJobNodeCommandPort(connectionString, SystemClock.Instance);

	internal override IWorkSessionCommandPort CreateSessionPort(string connectionString) =>
		new WorkSessionCommandPort(new SqliteWriteOperations(connectionString), SystemClock.Instance);

	internal override IAchievementCommandPort CreateAchievementPort(string connectionString) =>
		new SqliteAchievementCommandPort(connectionString, SystemClock.Instance);

	internal override IAuditQueryPort CreateAuditQueryPort(string connectionString) =>
		new AuditQueryPort(new SqliteReadOperations(connectionString), SystemClock.Instance);

	protected override object EncodeInstant(DateTimeOffset value) => value.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks;

	[Fact]
	public Task Concurrent_first_work_on_two_leaves_under_one_request_acknowledges_it_exactly_once() =>
		AssertConcurrentFirstWorkAcknowledgesExactlyOnceAsync();

	[Fact]
	public Task Concurrent_terminal_outcomes_on_two_leaves_under_one_request_acknowledge_it_exactly_once() =>
		AssertConcurrentTerminalOutcomeAcknowledgesExactlyOnceAsync();
}
