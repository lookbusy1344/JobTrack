namespace JobTrack.Persistence.Sqlite.Tests;

using System.Data.Common;
using Abstractions;
using Application.Ports;
using AwesomeAssertions;
using Database;
using Microsoft.Data.Sqlite;
using NodaTime;
using TestSupport;

public sealed class SqliteJobNodeCommandPortTests()
	: JobNodeCommandPortContractTestsBase(new SqliteDatabaseFixture())
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

	protected override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new SqliteInstallationBootstrapPort(connectionString, SystemClock.Instance);

	protected override IJobNodeCommandPort CreateCommandPort(string connectionString) =>
		new SqliteJobNodeCommandPort(connectionString, SystemClock.Instance);

	protected override IAuditQueryPort CreateAuditQueryPort(string connectionString) =>
		new SqliteAuditQueryPort(connectionString, SystemClock.Instance);

	protected override object EncodeInstant(DateTimeOffset value) => value.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks;

	/// <summary>
	///     Ownership model §4.3: pickup's correctness mechanism is the conditional
	///     <c>WHERE owner_user_id IS NULL</c> update, not a lock -- two concurrent claimants racing the
	///     same unassigned node must have exactly one win. SQLite's <c>BEGIN IMMEDIATE</c> serializes
	///     the two transactions, so the loser sees the winner's committed claim either at its own
	///     authorization check or at the conditional update, depending on interleaving.
	/// </summary>
	[Fact]
	public async Task Concurrent_pickups_of_the_same_unassigned_node_allow_exactly_one_to_succeed()
	{
		var (rootId, jobManagerId, workerA) = await SeedRootAndUsersAsync();
		var workerB = await SeedEmployeeAsync("Other Worker", "other.worker.pickup-race", EmployeeRole.Worker);
		var portA = CreateCommandPort(ConnectionString);
		var portB = CreateCommandPort(ConnectionString);
		var unassigned = await portA.AddChildAsync(new() {
			Context = new() { Actor = jobManagerId, CorrelationId = Guid.NewGuid() },
			ParentId = rootId,
			Description = "Unassigned pool leaf",
			OwnerUserId = null,
			Priority = Priority.Medium,
		});

		var results = await Task.WhenAll(
			TryPickUpAsync(portA, workerA, unassigned.Id),
			TryPickUpAsync(portB, workerB, unassigned.Id));

		results.Count(succeeded => succeeded).Should().Be(1);
	}

	/// <summary>
	///     The loser of the race can surface either exception depending on interleaving: the conditional
	///     <c>WHERE owner_user_id IS NULL</c> update losing after passing a stale authorization check
	///     (<see cref="InvariantViolationException" />, "job-node-already-claimed"), or a fresh
	///     authorization re-check already seeing the winner's committed claim
	///     (<see cref="AuthorizationDeniedException" />). Both mean "did not win the race".
	/// </summary>
	private static async Task<bool> TryPickUpAsync(IJobNodeCommandPort port, AppUserId actor, JobNodeId nodeId)
	{
		try {
			_ = await port.PickUpAsync(new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() }, NodeId = nodeId });
			return true;
		}
		catch (InvariantViolationException) {
			return false;
		}
		catch (AuthorizationDeniedException) {
			return false;
		}
	}
}
