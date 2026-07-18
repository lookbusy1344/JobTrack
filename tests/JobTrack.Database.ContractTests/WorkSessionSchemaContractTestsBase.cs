namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Shared TC-DB-SESSION-001 contract for schema slice 7: <c>work_session</c>,
///     interval ordering, active-session uniqueness, and same-user/same-leaf
///     non-overlap (impl plan §6.2 item 7, spec §4.4), asserted identically
///     against PostgreSQL and SQLite by <see cref="PostgreSqlWorkSessionSchemaTests" />
///     and <see cref="SqliteWorkSessionSchemaTests" />.
/// </summary>
public abstract class WorkSessionSchemaContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const short PriorityMedium = 2;

	private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

	private readonly IDisposableTestDatabase database;

	protected WorkSessionSchemaContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Deploying_creates_an_empty_work_session_table()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		(await CountRowsAsync(connection, "work_session")).Should().Be(0);
	}

	[Fact]
	public async Task Inserting_a_finished_session_with_finish_after_start_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");

		var id = await InsertSessionAsync(connection, leafWorkId, userId, Epoch, Epoch.AddHours(1));

		id.Should().BePositive();
		await AssertSessionRangeAsync(connection, id, Epoch, Epoch.AddHours(1));
	}

	[Fact]
	public async Task Inserting_an_unfinished_session_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");

		var id = await InsertSessionAsync(connection, leafWorkId, userId, Epoch, null);

		id.Should().BePositive();
		await AssertSessionRangeAsync(connection, id, Epoch, null);
	}

	[Fact]
	public async Task Inserting_a_session_finishing_at_or_before_it_started_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");

		var act = async () => await InsertSessionAsync(connection, leafWorkId, userId, Epoch, Epoch);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Overlapping_sessions_for_the_same_user_and_leaf_work_are_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		await InsertSessionAsync(connection, leafWorkId, userId, Epoch, Epoch.AddHours(2));

		var act = async () => await InsertSessionAsync(connection, leafWorkId, userId, Epoch.AddHours(1), Epoch.AddHours(3));

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task A_second_active_session_for_the_same_user_and_leaf_work_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		await InsertSessionAsync(connection, leafWorkId, userId, Epoch, null);

		var act = async () => await InsertSessionAsync(connection, leafWorkId, userId, Epoch.AddHours(5), null);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Sessions_touching_at_a_boundary_do_not_overlap()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		await InsertSessionAsync(connection, leafWorkId, userId, Epoch, Epoch.AddHours(1));

		var id = await InsertSessionAsync(connection, leafWorkId, userId, Epoch.AddHours(1), Epoch.AddHours(2));

		id.Should().BePositive();
	}

	[Fact]
	public async Task Overlapping_sessions_for_the_same_user_on_different_leaves_succeed()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, firstLeafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		var secondLeafWorkId = await AddLeafWorkUnderRootAsync(connection, userId);
		await InsertSessionAsync(connection, firstLeafWorkId, userId, Epoch, Epoch.AddHours(2));

		var id = await InsertSessionAsync(connection, secondLeafWorkId, userId, Epoch, Epoch.AddHours(2));

		id.Should().BePositive();
	}

	[Fact]
	public async Task Overlapping_sessions_for_different_users_on_the_same_leaf_work_succeed()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (firstUserId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		var (secondUserId, _) = await SeedAppUserAsync(connection, "Bob Example");
		await InsertSessionAsync(connection, leafWorkId, firstUserId, Epoch, Epoch.AddHours(2));

		var id = await InsertSessionAsync(connection, leafWorkId, secondUserId, Epoch, Epoch.AddHours(2));

		id.Should().BePositive();
	}

	[Fact]
	public async Task Concurrent_overlapping_sessions_for_the_same_user_and_leaf_work_allow_exactly_one_to_succeed()
	{
		await using var seedConnection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(seedConnection, "Alice Example");

		await using var connectionA = await OpenExistingConnectionAsync();
		await using var connectionB = await OpenExistingConnectionAsync();

		var results = await Task.WhenAll(
			TryInsertSessionAsync(connectionA, leafWorkId, userId, Epoch, Epoch.AddHours(2)),
			TryInsertSessionAsync(connectionB, leafWorkId, userId, Epoch.AddHours(1), Epoch.AddHours(3)));

		results.Count(succeeded => succeeded).Should().Be(1);
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	/// <summary>PostgreSQL binds <see cref="DateTimeOffset" /> directly; SQLite needs ADR 0007's unix-epoch-ticks encoding.</summary>
	protected abstract object EncodeInstant(DateTimeOffset value);

	/// <summary>
	///     Drift check for the generated <c>work_session.session_range</c> column (remediation plan
	///     §3.1): a no-op on providers with no such column. PostgreSQL overrides this to read the
	///     stored range back and assert it matches <paramref name="startedAt" />/<paramref name="finishedAt" />.
	/// </summary>
	protected virtual Task AssertSessionRangeAsync(DbConnection connection, long sessionId, DateTimeOffset startedAt, DateTimeOffset? finishedAt) =>
		Task.CompletedTask;

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

	private async Task<(long UserId, long LeafWorkId)> SeedUserAndLeafWorkAsync(DbConnection connection, string displayName)
	{
		var (userId, _) = await SeedAppUserAsync(connection, displayName);
		var leafWorkId = await AddLeafWorkUnderRootAsync(connection, userId);
		return (userId, leafWorkId);
	}

	private async Task<long> AddLeafWorkUnderRootAsync(DbConnection connection, long ownerUserId)
	{
		var rootId = await FindOrCreateRootAsync(connection, ownerUserId);
		var leafId = await InsertNodeAsync(connection, ownerUserId, rootId);
		await InsertLeafWorkAsync(connection, leafId);
		return leafId;
	}

	private static async Task<long?> TryFindRootAsync(DbConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT id FROM job_node WHERE parent_id IS NULL;";
		var result = await command.ExecuteScalarAsync();
		return result is null ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
	}

	private async Task<long> FindOrCreateRootAsync(DbConnection connection, long ownerUserId)
	{
		var existingRootId = await TryFindRootAsync(connection);
		return existingRootId ?? await InsertNodeAsync(connection, ownerUserId, null);
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

	private async Task<long> InsertSessionAsync(
		DbConnection connection, long leafWorkId, long workedByUserId, DateTimeOffset startedAt, DateTimeOffset? finishedAt)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO work_session (leaf_work_id, worked_by_user_id, started_at, finished_at, changed_at)
							  VALUES (@leafWorkId, @workedByUserId, @startedAt, @finishedAt, @changedAt)
							  RETURNING id;
							  """;
		AddParameter(command, "@leafWorkId", leafWorkId);
		AddParameter(command, "@workedByUserId", workedByUserId);
		AddParameter(command, "@startedAt", EncodeInstant(startedAt));
		AddParameter(command, "@finishedAt", finishedAt is null ? DBNull.Value : EncodeInstant(finishedAt.Value));
		AddParameter(command, "@changedAt", EncodeInstant(DateTimeOffset.UtcNow));

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<bool> TryInsertSessionAsync(
		DbConnection connection, long leafWorkId, long workedByUserId, DateTimeOffset startedAt, DateTimeOffset? finishedAt)
	{
		try {
			await InsertSessionAsync(connection, leafWorkId, workedByUserId, startedAt, finishedAt);
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
