namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using Abstractions;
using Application.Ports;
using AwesomeAssertions;
using Database;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlWorkSessionCommandPortTests()
	: WorkSessionCommandPortContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new PostgreSqlInstallationBootstrapPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());

	protected override IJobNodeCommandPort CreateJobNodePort(string connectionString) =>
		new PostgreSqlJobNodeCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());

	protected override IWorkSessionCommandPort CreateSessionPort(string connectionString) =>
		new PostgreSqlWorkSessionCommandPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());

	protected override IAuditQueryPort CreateAuditQueryPort(string connectionString) =>
		new PostgreSqlAuditQueryPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build());

	/// <summary>
	///     There is no advisory lock domain for work sessions (ADR 0012): schema version 0007's GiST
	///     exclusion constraint plus partial unique index is the sole mutual-exclusion mechanism, so this
	///     proves it holds under genuine PostgreSQL MVCC interleaving, not just single-threaded sequencing.
	/// </summary>
	[Fact]
	public async Task Concurrent_session_starts_for_the_same_worker_and_leaf_allow_exactly_one_to_succeed()
	{
		var (_, _, workerId, leafId) = await SeedReadyLeafAsync();
		var portA = CreateSessionPort(ConnectionString);
		var portB = CreateSessionPort(ConnectionString);

		var results = await Task.WhenAll(
			TryStartSessionAsync(portA, workerId, leafId),
			TryStartSessionAsync(portB, workerId, leafId));

		results.Count(succeeded => succeeded).Should().Be(1);
	}

	private static async Task<bool> TryStartSessionAsync(IWorkSessionCommandPort port, AppUserId workerId, JobNodeId leafId)
	{
		try {
			_ = await port.StartSessionAsync(new() { Context = ContextFor(workerId), LeafWorkId = leafId, WorkedByUserId = workerId });
			return true;
		}
		catch (InvariantViolationException) {
			return false;
		}
	}
}
