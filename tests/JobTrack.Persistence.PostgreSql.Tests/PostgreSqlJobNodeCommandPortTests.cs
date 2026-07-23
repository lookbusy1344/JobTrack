namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using NodaTime;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlJobNodeCommandPortTests()
	: JobNodeCommandPortContractTestsBase(new PostgreSqlDatabaseFixture())
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

	internal override IAuditQueryPort CreateAuditQueryPort(string connectionString) =>
		new PostgreSqlAuditQueryPort(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(), SystemClock.Instance);

	protected override object EncodeInstant(DateTimeOffset value) => value;

	/// <summary>
	///     ADR 0012's race, exercised through the real command port rather than raw SQL (unlike
	///     <c>HierarchyMoveSchemaContractTestsBase</c>'s schema-level equivalent): two concurrent
	///     opposing moves that would each close a cycle acquire schema version 0016's
	///     <c>move_job_node</c> advisory locks in the same ascending order regardless of which port
	///     instance calls it, so exactly one commits and the other's deferred cycle trigger rejects
	///     its move at commit.
	/// </summary>
	[Fact]
	public async Task Concurrent_opposing_moves_that_would_create_a_cycle_allow_exactly_one_to_succeed()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var portA = CreateCommandPort(ConnectionString);
		var portB = CreateCommandPort(ConnectionString);

		var firstParent = await portA.AddChildAsync(CreateRequest(jobManagerId, rootId));
		var secondParent = await portA.AddChildAsync(CreateRequest(jobManagerId, rootId));
		var firstChild = await portA.AddChildAsync(CreateRequest(jobManagerId, firstParent.Id));
		var secondChild = await portA.AddChildAsync(CreateRequest(jobManagerId, secondParent.Id));

		var results = await Task.WhenAll(
			TryMoveAsync(portA, jobManagerId, firstParent, secondChild.Id),
			TryMoveAsync(portB, jobManagerId, secondParent, firstChild.Id));

		results.Count(succeeded => succeeded).Should().Be(1);
	}

	/// <summary>
	///     job_node_parent_has_no_leaf_work (schema version 0006) is a <c>DEFERRABLE INITIALLY DEFERRED</c>
	///     constraint trigger: it only fires at <c>COMMIT</c>, after <c>SaveChangesAsync</c> has already
	///     succeeded. This proves the port translates that commit-time failure into a
	///     <see cref="JobTrackException" /> rather than letting a raw provider exception cross the facade.
	/// </summary>
	[Fact]
	public async Task Creating_a_child_under_a_node_that_holds_leaf_work_throws_an_invariant_violation()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var port = CreateCommandPort(ConnectionString);
		var leaf = await port.AddChildAsync(CreateRequest(jobManagerId, rootId));
		await using var connection = new NpgsqlConnection(ConnectionString);
		await connection.OpenAsync();
		await using (var command = connection.CreateCommand()) {
			command.CommandText = "INSERT INTO leaf_work (job_node_id) VALUES (@jobNodeId);";
			command.Parameters.AddWithValue("jobNodeId", leaf.Id.Value);
			_ = await command.ExecuteNonQueryAsync();
		}

		var act = () => port.AddChildAsync(CreateRequest(jobManagerId, leaf.Id));

		await act.Should().ThrowAsync<InvariantViolationException>();
	}

	/// <summary>
	///     ADR 0012's other proven race (spike 02-prerequisite-cycle.sql): two concurrent edge inserts
	///     that are each individually acyclic from their own transaction's point of view (A-&gt;B and
	///     B-&gt;A submitted at the same time) can otherwise both commit and jointly create a cycle. The
	///     <c>jobtrack:prerequisite-graph-writes</c> advisory lock inside <c>add_job_prerequisite</c>
	///     serializes them, so exactly one commits and the other's deferred cycle trigger rejects it.
	/// </summary>
	[Fact]
	public async Task Concurrent_opposing_prerequisite_edges_that_would_create_a_cycle_allow_exactly_one_to_succeed()
	{
		var (rootId, jobManagerId, _) = await SeedRootAndUsersAsync();
		var portA = CreateCommandPort(ConnectionString);
		var portB = CreateCommandPort(ConnectionString);
		var a = await portA.AddChildAsync(CreateRequest(jobManagerId, rootId));
		var b = await portA.AddChildAsync(CreateRequest(jobManagerId, rootId));

		var results = await Task.WhenAll(
			TryAddPrerequisiteAsync(portA, jobManagerId, a.Id, b.Id),
			TryAddPrerequisiteAsync(portB, jobManagerId, b.Id, a.Id));

		results.Count(succeeded => succeeded).Should().Be(1);
	}

	/// <summary>
	///     Ownership model §4.3: pickup's correctness mechanism is the conditional
	///     <c>WHERE owner_user_id IS NULL</c> update, not a lock -- two concurrent claimants racing the
	///     same unassigned node must have exactly one win, the other seeing zero rows affected and
	///     throwing <c>job-node-already-claimed</c> rather than silently overwriting the winner's claim.
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
	///     (<see cref="AuthorizationDeniedException" />) if it starts after the winner has already
	///     committed. Both mean "did not win the race" -- <see cref="JobPickupPolicy" /> denies pickup of
	///     an already-owned node identically regardless of which side of the race caused it.
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

	private static async Task<bool> TryAddPrerequisiteAsync(
		IJobNodeCommandPort port, AppUserId actor, JobNodeId requiredJobId, JobNodeId dependentJobId)
	{
		try {
			await port.AddPrerequisiteAsync(new() {
				Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() },
				RequiredJobId = requiredJobId,
				DependentJobId = dependentJobId,
			});
			return true;
		}
		catch (InvariantViolationException) {
			return false;
		}
	}

	private static CreateJobNodeRequest CreateRequest(AppUserId actor, JobNodeId parentId) => new() {
		Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() },
		ParentId = parentId,
		Description = "Do the thing",
		OwnerUserId = actor,
		Priority = Priority.Medium,
	};

	private static async Task<bool> TryMoveAsync(
		IJobNodeCommandPort port, AppUserId actor, JobNodeResult node, JobNodeId newParentId)
	{
		try {
			_ = await port.MoveAsync(new() {
				Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() },
				NodeId = node.Id,
				NewParentId = newParentId,
				Version = node.Version,
			});
			return true;
		}
		catch (InvariantViolationException) {
			return false;
		}
	}
}
