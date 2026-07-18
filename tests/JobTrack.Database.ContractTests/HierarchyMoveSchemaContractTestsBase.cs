namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Shared TC-DB-HIER-002 contract for schema slice 5: <c>job_node</c> cycle
///     prevention and atomic move semantics (impl plan §6.2 item 5, ADR 0012),
///     asserted identically against PostgreSQL and SQLite by
///     <see cref="PostgreSqlHierarchyMoveSchemaTests" /> and
///     <see cref="SqliteHierarchyMoveSchemaTests" />. Reachability from the root
///     is a corollary of slice 4's single-root/FK-restrict invariants plus this
///     slice's acyclicity guard, not a separate mechanism -- asserted explicitly
///     here rather than left implicit. Revalidation of prerequisite edges
///     affected by a move is out of scope until slice 8's <c>job_prerequisite</c>
///     table exists.
/// </summary>
public abstract class HierarchyMoveSchemaContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const short PriorityMedium = 2;

	private readonly IDisposableTestDatabase database;

	protected HierarchyMoveSchemaContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Moving_a_node_to_a_new_valid_parent_succeeds_and_updates_parent_and_row_version()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var sourceParentId = await InsertNodeAsync(connection, adminId, rootId);
		var destinationParentId = await InsertNodeAsync(connection, adminId, rootId);
		var movingId = await InsertNodeAsync(connection, adminId, sourceParentId);

		await MoveNodeAsync(connection, movingId, destinationParentId);

		var (parentId, rowVersion) = await ReadParentAndRowVersionAsync(connection, movingId);
		parentId.Should().Be(destinationParentId);
		rowVersion.Should().Be(2);
	}

	[Fact]
	public async Task Moving_a_node_under_its_own_direct_child_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var parentId = await InsertNodeAsync(connection, adminId, rootId);
		var childId = await InsertNodeAsync(connection, adminId, parentId);

		var act = async () => await MoveNodeAsync(connection, parentId, childId);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Moving_a_node_under_a_deeper_descendant_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var ancestorId = await InsertNodeAsync(connection, adminId, rootId);
		var middleId = await InsertNodeAsync(connection, adminId, ancestorId);
		var deepDescendantId = await InsertNodeAsync(connection, adminId, middleId);

		var act = async () => await MoveNodeAsync(connection, ancestorId, deepDescendantId);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Every_node_remains_reachable_from_root_after_a_valid_move()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var sourceParentId = await InsertNodeAsync(connection, adminId, rootId);
		var destinationParentId = await InsertNodeAsync(connection, adminId, rootId);
		var movingId = await InsertNodeAsync(connection, adminId, sourceParentId);
		_ = await InsertNodeAsync(connection, adminId, movingId);

		(await CountRowsAsync(connection, "job_node")).Should().Be(await CountReachableFromRootAsync(connection, rootId));

		await MoveNodeAsync(connection, movingId, destinationParentId);

		(await CountRowsAsync(connection, "job_node")).Should().Be(await CountReachableFromRootAsync(connection, rootId));
	}

	[Fact]
	public async Task Concurrent_opposing_moves_that_would_create_a_cycle_allow_exactly_one_to_succeed()
	{
		await using var seedConnection = await OpenDeployedConnectionAsync();
		var (adminId, _) = await SeedAppUserAsync(seedConnection, "Alice Example");
		var rootId = await InsertNodeAsync(seedConnection, adminId, null);
		var firstParentId = await InsertNodeAsync(seedConnection, adminId, rootId);
		var secondParentId = await InsertNodeAsync(seedConnection, adminId, rootId);
		var firstChildId = await InsertNodeAsync(seedConnection, adminId, firstParentId);
		var secondChildId = await InsertNodeAsync(seedConnection, adminId, secondParentId);

		await using var connectionA = await OpenExistingConnectionAsync();
		await using var connectionB = await OpenExistingConnectionAsync();

		var results = await Task.WhenAll(
			TryMoveNodeAsync(connectionA, firstParentId, secondChildId),
			TryMoveNodeAsync(connectionB, secondParentId, firstChildId));

		results.Count(succeeded => succeeded).Should().Be(1);
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	/// <summary>PostgreSQL binds <see cref="DateTimeOffset" /> directly; SQLite needs ADR 0007's unix-epoch-ticks encoding.</summary>
	protected abstract object EncodeInstant(DateTimeOffset value);

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

	private static async Task<long> CountReachableFromRootAsync(DbConnection connection, long rootId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  WITH RECURSIVE reachable(id) AS (
							  SELECT id FROM job_node WHERE id = @rootId
							  UNION ALL
							  SELECT jn.id FROM job_node jn JOIN reachable r ON jn.parent_id = r.id
							  )
							  SELECT COUNT(*) FROM reachable;
							  """;
		AddParameter(command, "@rootId", rootId);
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static async Task<(long ParentId, long RowVersion)> ReadParentAndRowVersionAsync(DbConnection connection, long id)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT parent_id, row_version FROM job_node WHERE id = {id};";

		await using var reader = await command.ExecuteReaderAsync();
		_ = await reader.ReadAsync();
		return (reader.GetInt64(0), reader.GetInt64(1));
	}

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}
