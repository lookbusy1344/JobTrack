namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Shared TC-DB-LEAF-001 contract for schema slice 6: <c>leaf_work</c> and
///     leaf/branch/root exclusivity (impl plan §6.2 item 6, spec §4.2 rules
///     7-10, §4.3), asserted identically against PostgreSQL and SQLite by
///     <see cref="PostgreSqlLeafWorkSchemaTests" /> and
///     <see cref="SqliteLeafWorkSchemaTests" />. Atomic decomposition of an
///     already-worked leaf (spec §4.5, TC-DB-LEAF-002) needs <c>work_session</c>
///     (plan §6.2 item 7) and is out of scope here.
/// </summary>
public abstract class LeafWorkSchemaContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const short PriorityMedium = 2;
	private const short AchievementWaiting = 1;

	private readonly IDisposableTestDatabase database;

	protected LeafWorkSchemaContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Deploying_creates_an_empty_leaf_work_table()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		(await CountRowsAsync(connection, "leaf_work")).Should().Be(0);
	}

	[Fact]
	public async Task Attaching_leaf_work_to_a_leaf_succeeds_with_default_waiting_achievement()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var leafId = await InsertNodeAsync(connection, adminId, rootId);

		await InsertLeafWorkAsync(connection, leafId);

		(await ReadAchievementIdAsync(connection, leafId)).Should().Be(AchievementWaiting);
	}

	[Fact]
	public async Task Attaching_leaf_work_to_the_root_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);

		var act = async () => await InsertLeafWorkAsync(connection, rootId);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Attaching_leaf_work_to_a_node_with_children_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var branchId = await InsertNodeAsync(connection, adminId, rootId);
		_ = await InsertNodeAsync(connection, adminId, branchId);

		var act = async () => await InsertLeafWorkAsync(connection, branchId);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Adding_a_child_to_a_node_that_holds_leaf_work_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var leafId = await InsertNodeAsync(connection, adminId, rootId);
		await InsertLeafWorkAsync(connection, leafId);

		var act = async () => await InsertNodeAsync(connection, adminId, leafId);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task A_second_leaf_work_row_for_the_same_node_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var leafId = await InsertNodeAsync(connection, adminId, rootId);
		await InsertLeafWorkAsync(connection, leafId);

		var act = async () => await InsertLeafWorkAsync(connection, leafId);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Concurrent_leaf_work_attachment_and_child_insertion_on_the_same_node_allow_exactly_one_to_succeed()
	{
		await using var seedConnection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(seedConnection, "Alice Example");
		var rootId = await InsertNodeAsync(seedConnection, adminId, null);
		var leafId = await InsertNodeAsync(seedConnection, adminId, rootId);

		await using var connectionA = await OpenExistingConnectionAsync();
		await using var connectionB = await OpenExistingConnectionAsync();

		var results = await Task.WhenAll(
			TryInsertLeafWorkAsync(connectionA, leafId),
			TryInsertChildAsync(connectionB, adminId, leafId));

		results.Count(succeeded => succeeded).Should().Be(1);
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	/// <summary>PostgreSQL binds <see cref="DateTimeOffset" /> directly; SQLite needs ADR 0007's unix-epoch-ticks encoding.</summary>
	protected abstract object EncodeInstant(DateTimeOffset value);

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

	private async Task InsertLeafWorkAsync(DbConnection connection, long jobNodeId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO leaf_work (job_node_id, changed_at)
							  VALUES (@jobNodeId, @changedAt);
							  """;
		AddParameter(command, "@jobNodeId", jobNodeId);
		AddParameter(command, "@changedAt", EncodeInstant(DateTimeOffset.UtcNow));

		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task<bool> TryInsertLeafWorkAsync(DbConnection connection, long jobNodeId)
	{
		try {
			await InsertLeafWorkAsync(connection, jobNodeId);
			return true;
		}
		catch (DbException) {
			return false;
		}
	}

	private async Task<bool> TryInsertChildAsync(DbConnection connection, long ownerUserId, long parentId)
	{
		try {
			_ = await InsertNodeAsync(connection, ownerUserId, parentId);
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

	private static async Task<long> ReadAchievementIdAsync(DbConnection connection, long jobNodeId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT achievement_id FROM leaf_work WHERE job_node_id = @jobNodeId;";
		AddParameter(command, "@jobNodeId", jobNodeId);

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
