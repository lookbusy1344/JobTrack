namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Shared TC-DB-PREREQ-001/002 contract for schema slice 8:
///     <c>job_prerequisite</c>, DAG enforcement, and hierarchy-edge exclusion,
///     including revalidation on move (impl plan §6.2 item 8, spec §6),
///     asserted identically against PostgreSQL and SQLite by
///     <see cref="PostgreSqlJobPrerequisiteSchemaTests" /> and
///     <see cref="SqliteJobPrerequisiteSchemaTests" />. Readiness/eligibility
///     queries need achievement derivation (schema slice 13) and are out of
///     scope here.
/// </summary>
public abstract class JobPrerequisiteSchemaContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const short PriorityMedium = 2;

	private readonly IDisposableTestDatabase database;

	protected JobPrerequisiteSchemaContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Deploying_creates_an_empty_job_prerequisite_table()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		(await CountRowsAsync(connection, "job_prerequisite")).Should().Be(0);
	}

	[Fact]
	public async Task Adding_a_valid_prerequisite_edge_between_unrelated_jobs_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var requiredId = await InsertNodeAsync(connection, adminId, rootId);
		var dependentId = await InsertNodeAsync(connection, adminId, rootId);

		var act = async () => await AddPrerequisiteAsync(connection, requiredId, dependentId);

		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task A_node_requiring_itself_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var nodeId = await InsertNodeAsync(connection, adminId, rootId);

		var act = async () => await AddPrerequisiteAsync(connection, nodeId, nodeId);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task A_duplicate_prerequisite_edge_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var requiredId = await InsertNodeAsync(connection, adminId, rootId);
		var dependentId = await InsertNodeAsync(connection, adminId, rootId);
		await AddPrerequisiteAsync(connection, requiredId, dependentId);

		var act = async () => await AddPrerequisiteAsync(connection, requiredId, dependentId);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task A_direct_two_node_cycle_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var aId = await InsertNodeAsync(connection, adminId, rootId);
		var bId = await InsertNodeAsync(connection, adminId, rootId);
		await AddPrerequisiteAsync(connection, aId, bId);

		var act = async () => await AddPrerequisiteAsync(connection, bId, aId);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task A_longer_cycle_through_three_nodes_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var aId = await InsertNodeAsync(connection, adminId, rootId);
		var bId = await InsertNodeAsync(connection, adminId, rootId);
		var cId = await InsertNodeAsync(connection, adminId, rootId);
		await AddPrerequisiteAsync(connection, aId, bId);
		await AddPrerequisiteAsync(connection, bId, cId);

		var act = async () => await AddPrerequisiteAsync(connection, cId, aId);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task An_edge_where_the_required_job_is_an_ancestor_of_the_dependent_job_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var ancestorId = await InsertNodeAsync(connection, adminId, rootId);
		var descendantId = await InsertNodeAsync(connection, adminId, ancestorId);

		var act = async () => await AddPrerequisiteAsync(connection, ancestorId, descendantId);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task An_edge_where_the_required_job_is_a_descendant_of_the_dependent_job_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var ancestorId = await InsertNodeAsync(connection, adminId, rootId);
		var descendantId = await InsertNodeAsync(connection, adminId, ancestorId);

		var act = async () => await AddPrerequisiteAsync(connection, descendantId, ancestorId);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Moving_a_node_that_would_turn_an_edge_into_a_hierarchy_edge_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var requiredId = await InsertNodeAsync(connection, adminId, rootId);
		var dependentId = await InsertNodeAsync(connection, adminId, rootId);
		await AddPrerequisiteAsync(connection, requiredId, dependentId);

		var act = async () => await MoveNodeAsync(connection, dependentId, requiredId);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Moving_a_node_unrelated_to_any_prerequisite_edge_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var requiredId = await InsertNodeAsync(connection, adminId, rootId);
		var dependentId = await InsertNodeAsync(connection, adminId, rootId);
		await AddPrerequisiteAsync(connection, requiredId, dependentId);
		var otherParentId = await InsertNodeAsync(connection, adminId, rootId);
		var unrelatedId = await InsertNodeAsync(connection, adminId, rootId);

		var act = async () => await MoveNodeAsync(connection, unrelatedId, otherParentId);

		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task Concurrent_opposing_edges_that_would_create_a_cycle_allow_exactly_one_to_succeed()
	{
		await using var seedConnection = await OpenDeployedConnectionAsync();
		var (adminId, _) = await SeedAppUserAsync(seedConnection, "Alice Example");
		var rootId = await InsertNodeAsync(seedConnection, adminId, null);
		var aId = await InsertNodeAsync(seedConnection, adminId, rootId);
		var bId = await InsertNodeAsync(seedConnection, adminId, rootId);

		await using var connectionA = await OpenExistingConnectionAsync();
		await using var connectionB = await OpenExistingConnectionAsync();

		var results = await Task.WhenAll(
			TryAddPrerequisiteAsync(connectionA, aId, bId),
			TryAddPrerequisiteAsync(connectionB, bId, aId));

		results.Count(succeeded => succeeded).Should().Be(1);
	}

	[Fact]
	public async Task Concurrent_move_and_prerequisite_edge_that_would_jointly_violate_ancestor_descendant_exclusion_allow_exactly_one_to_succeed()
	{
		await using var seedConnection = await OpenDeployedConnectionAsync();
		var (adminId, _) = await SeedAppUserAsync(seedConnection, "Alice Example");
		var rootId = await InsertNodeAsync(seedConnection, adminId, null);
		var yId = await InsertNodeAsync(seedConnection, adminId, rootId);
		var mId = await InsertNodeAsync(seedConnection, adminId, rootId);

		await using var connectionA = await OpenExistingConnectionAsync();
		await using var connectionB = await OpenExistingConnectionAsync();

		var results = await Task.WhenAll(
			TryMoveNodeAsync(connectionA, mId, yId),
			TryAddPrerequisiteAsync(connectionB, mId, yId));

		results.Count(succeeded => succeeded).Should().Be(1);
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	/// <summary>PostgreSQL binds <see cref="DateTimeOffset" /> directly; SQLite needs ADR 0007's unix-epoch-ticks encoding.</summary>
	protected abstract object EncodeInstant(DateTimeOffset value);

	/// <summary>PostgreSQL invokes the <c>add_job_prerequisite</c> stored function; SQLite issues the equivalent raw parameterized INSERT.</summary>
	protected abstract Task AddPrerequisiteAsync(DbConnection connection, long fromId, long toId);

	/// <summary>PostgreSQL invokes the <c>move_job_node</c> stored function; SQLite issues the equivalent raw parameterized UPDATE.</summary>
	protected abstract Task MoveNodeAsync(DbConnection connection, long nodeId, long newParentId);

	private async Task<DbConnection> OpenDeployedConnectionAsync()
	{
		var connection = await OpenExistingConnectionAsync();

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
		var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);

		return connection;
	}

	private async Task<DbConnection> OpenExistingConnectionAsync()
	{
		var connection = CreateConnection(database.ConnectionString);
		await connection.OpenAsync();
		await PrepareConnectionAsync(connection);
		return connection;
	}

	private static async Task<(long AppUserId, long IdentityUserId)> SeedAppUserAsync(DbConnection connection, string displayName)
	{
		await using var appUserCommand = connection.CreateCommand();
		appUserCommand.CommandText = """
									 INSERT INTO app_user (display_name, iana_time_zone)
									 VALUES (@displayName, 'Europe/London')
									 RETURNING id;
									 """;
		AddParameter(appUserCommand, "@displayName", displayName);
		var appUserId = Convert.ToInt64(await appUserCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

		return (appUserId, 0);
	}

	private async Task<long> InsertNodeAsync(DbConnection connection, long ownerUserId, long? parentId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_node
							  (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
							  VALUES
							  (@parentId, @description, @ownerUserId, @ownerUserId, @priorityId, @postedAt)
							  RETURNING id;
							  """;
		AddParameter(command, "@parentId", (object?)parentId ?? DBNull.Value);
		AddParameter(command, "@description", "A job");
		AddParameter(command, "@ownerUserId", ownerUserId);
		AddParameter(command, "@priorityId", PriorityMedium);
		AddParameter(command, "@postedAt", EncodeInstant(DateTimeOffset.UtcNow));

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<bool> TryAddPrerequisiteAsync(DbConnection connection, long fromId, long toId)
	{
		try {
			await AddPrerequisiteAsync(connection, fromId, toId);
			return true;
		}
		catch (DbException) {
			return false;
		}
	}

	private async Task<bool> TryMoveNodeAsync(DbConnection connection, long nodeId, long newParentId)
	{
		try {
			await MoveNodeAsync(connection, nodeId, newParentId);
			return true;
		}
		catch (DbException) {
			return false;
		}
	}

	private static async Task<long> CountRowsAsync(DbConnection connection, string tableName)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}
