namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Shared TC-DB-HIER-001 (database column) contract for schema slice 4:
///     <c>job_node</c>, the seeded <c>priority</c> reference table, and the
///     permanent-root guard, asserted identically against PostgreSQL and SQLite
///     by <see cref="PostgreSqlJobNodeSchemaTests" /> and
///     <see cref="SqliteJobNodeSchemaTests" /> (impl plan §6.2 item 4, ADR 0015,
///     ADR 0021). Hierarchy acyclicity/move validation (item 5) and leaf/branch
///     exclusivity (item 6) are out of scope for this contract.
/// </summary>
public abstract class JobNodeSchemaContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private const short PriorityMedium = 2;

	private static readonly IReadOnlyList<string> SeededPriorityNames = ["Low", "Medium", "High", "Urgent"];

	private readonly IDisposableTestDatabase database;

	protected JobNodeSchemaContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Deploying_creates_an_empty_job_node_table_and_seeded_priorities()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		(await CountRowsAsync(connection, "job_node")).Should().Be(0);
		(await ReadPriorityNamesAsync(connection)).Should().BeEquivalentTo(SeededPriorityNames);
	}

	[Fact]
	public async Task Inserting_a_root_and_a_child_node_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var childId = await InsertNodeAsync(connection, adminId, rootId);

		childId.Should().BePositive();
	}

	[Fact]
	public async Task A_second_simultaneous_root_is_rejected_regardless_of_initialisation_state()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertNodeAsync(connection, adminId, null);

		var act = async () => await InsertNodeAsync(connection, adminId, null);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Self_parent_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);

		var act = async () => await ExecuteNonQueryAsync(connection, $"UPDATE job_node SET parent_id = {rootId} WHERE id = {rootId};");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Needed_finish_not_after_needed_start_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var now = DateTimeOffset.UtcNow;

		var act = async () => await InsertNodeAsync(
			connection, adminId, null, neededStart: now, neededFinish: now);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Negative_expected_cost_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");

		var act = async () => await InsertNodeAsync(connection, adminId, null, expectedCost: -1m);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Blank_description_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");

		var act = async () => await InsertNodeAsync(connection, adminId, null, "   ");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Deleting_the_root_before_initialisation_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);

		await ExecuteNonQueryAsync(connection, $"DELETE FROM job_node WHERE id = {rootId};");

		(await CountRowsAsync(connection, "job_node")).Should().Be(0);
	}

	[Fact]
	public async Task Deleting_the_root_after_initialisation_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		await MarkInitialisedAsync(connection);

		var act = async () => await ExecuteNonQueryAsync(connection, $"DELETE FROM job_node WHERE id = {rootId};");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Reparenting_the_root_after_initialisation_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var branchId = await InsertNodeAsync(connection, adminId, rootId);
		await MarkInitialisedAsync(connection);

		var act = async () => await ExecuteNonQueryAsync(
			connection, $"UPDATE job_node SET parent_id = {branchId} WHERE id = {rootId};");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task An_ordinary_update_on_the_root_after_initialisation_still_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		await MarkInitialisedAsync(connection);

		await ExecuteNonQueryAsync(connection, $"UPDATE job_node SET description = 'Renamed root' WHERE id = {rootId};");

		(await ReadDescriptionAsync(connection, rootId)).Should().Be("Renamed root");
	}

	[Fact]
	public async Task Inserting_a_non_root_node_with_a_null_owner_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var childId = await InsertNodeWithOwnerAsync(connection, adminId, null, rootId);

		childId.Should().BePositive();
	}

	[Fact]
	public async Task Updating_a_non_root_node_to_a_null_owner_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var childId = await InsertNodeAsync(connection, adminId, rootId);

		await ExecuteNonQueryAsync(connection, $"UPDATE job_node SET owner_user_id = NULL WHERE id = {childId};");

		(await ReadOwnerUserIdAsync(connection, childId)).Should().BeNull();
	}

	[Fact]
	public async Task Inserting_the_root_with_a_null_owner_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");

		var act = async () => await InsertNodeWithOwnerAsync(connection, adminId, null, null);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Updating_the_root_to_a_null_owner_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);

		var act = async () => await ExecuteNonQueryAsync(connection, $"UPDATE job_node SET owner_user_id = NULL WHERE id = {rootId};");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Deleting_an_app_user_referenced_as_owner_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertNodeAsync(connection, adminId, null);

		var act = async () => await ExecuteNonQueryAsync(connection, $"DELETE FROM app_user WHERE id = {adminId};");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Deleting_a_referenced_priority_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertNodeAsync(connection, adminId, null);

		var act = async () => await ExecuteNonQueryAsync(connection, $"DELETE FROM priority WHERE id = {PriorityMedium};");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Concurrent_delete_and_reparent_attempts_on_the_initialised_root_are_both_rejected()
	{
		await using var seedConnection = await OpenDeployedConnectionAsync();

		var (adminId, _) = await SeedAppUserAsync(seedConnection, "Alice Example");
		var rootId = await InsertNodeAsync(seedConnection, adminId, null);
		var branchId = await InsertNodeAsync(seedConnection, adminId, rootId);
		await MarkInitialisedAsync(seedConnection);

		await using var connectionA = await OpenExistingConnectionAsync();
		await using var connectionB = await OpenExistingConnectionAsync();

		var results = await Task.WhenAll(
			TryExecuteNonQueryAsync(connectionA, $"DELETE FROM job_node WHERE id = {rootId};"),
			TryExecuteNonQueryAsync(connectionB, $"UPDATE job_node SET parent_id = {branchId} WHERE id = {rootId};"));

		results.Should().OnlyContain(succeeded => !succeeded);
		(await CountRowsAsync(seedConnection, "job_node")).Should().Be(2);
	}

	[Fact]
	public async Task Concurrent_root_inserts_apply_exactly_once()
	{
		await using var seedConnection = await OpenDeployedConnectionAsync();
		var (adminId, _) = await SeedAppUserAsync(seedConnection, "Alice Example");

		await using var connectionA = await OpenExistingConnectionAsync();
		await using var connectionB = await OpenExistingConnectionAsync();

		var results = await Task.WhenAll(
			TryInsertNodeAsync(connectionA, adminId, null),
			TryInsertNodeAsync(connectionB, adminId, null));

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

	private async Task<long> InsertNodeAsync(
		DbConnection connection,
		long ownerUserId,
		long? parentId,
		string description = "A job",
		decimal? expectedCost = null,
		DateTimeOffset? neededStart = null,
		DateTimeOffset? neededFinish = null) =>
		await TryInsertNodeCoreAsync(connection, ownerUserId, parentId, description, expectedCost, neededStart, neededFinish)
		?? throw new InvalidOperationException("Expected the insert to return a generated id.");

	private async Task<bool> TryInsertNodeAsync(DbConnection connection, long ownerUserId, long? parentId)
	{
		try {
			await InsertNodeAsync(connection, ownerUserId, parentId);
			return true;
		}
		catch (DbException) {
			return false;
		}
	}

	private async Task<long?> TryInsertNodeCoreAsync(
		DbConnection connection,
		long ownerUserId,
		long? parentId,
		string description,
		decimal? expectedCost,
		DateTimeOffset? neededStart,
		DateTimeOffset? neededFinish)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_node
							  (parent_id, description, posted_by_user_id, owner_user_id, expected_cost, needed_start, needed_finish,
							   priority_id, posted_at)
							  VALUES
							  (@parentId, @description, @ownerUserId, @ownerUserId, @expectedCost, @neededStart, @neededFinish,
							   @priorityId, @postedAt)
							  RETURNING id;
							  """;
		AddParameter(command, "@parentId", (object?)parentId ?? DBNull.Value);
		AddParameter(command, "@description", description);
		AddParameter(command, "@ownerUserId", ownerUserId);
		AddParameter(command, "@expectedCost", (object?)expectedCost ?? DBNull.Value);
		AddParameter(command, "@neededStart", neededStart is null ? DBNull.Value : EncodeInstant(neededStart.Value));
		AddParameter(command, "@neededFinish", neededFinish is null ? DBNull.Value : EncodeInstant(neededFinish.Value));
		AddParameter(command, "@priorityId", PriorityMedium);
		AddParameter(command, "@postedAt", EncodeInstant(DateTimeOffset.UtcNow));

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<long> InsertNodeWithOwnerAsync(
		DbConnection connection,
		long postedByUserId,
		long? ownerUserId,
		long? parentId,
		string description = "A job")
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_node
							  (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
							  VALUES
							  (@parentId, @description, @postedByUserId, @ownerUserId, @priorityId, @postedAt)
							  RETURNING id;
							  """;
		AddParameter(command, "@parentId", (object?)parentId ?? DBNull.Value);
		AddParameter(command, "@description", description);
		AddParameter(command, "@postedByUserId", postedByUserId);
		AddParameter(command, "@ownerUserId", (object?)ownerUserId ?? DBNull.Value);
		AddParameter(command, "@priorityId", PriorityMedium);
		AddParameter(command, "@postedAt", EncodeInstant(DateTimeOffset.UtcNow));

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static async Task<long?> ReadOwnerUserIdAsync(DbConnection connection, long id)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT owner_user_id FROM job_node WHERE id = {id};";
		var value = await command.ExecuteScalarAsync();
		return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
	}

	private async Task MarkInitialisedAsync(DbConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "INSERT INTO initialised_marker (id, initialised_at) VALUES (1, @initialisedAt);";
		AddParameter(command, "@initialisedAt", EncodeInstant(DateTimeOffset.UtcNow));
		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task ExecuteNonQueryAsync(DbConnection connection, string commandText)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = commandText;
		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task<bool> TryExecuteNonQueryAsync(DbConnection connection, string commandText)
	{
		try {
			await ExecuteNonQueryAsync(connection, commandText);
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

	private static async Task<string> ReadDescriptionAsync(DbConnection connection, long id)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT description FROM job_node WHERE id = {id};";
		return (string)(await command.ExecuteScalarAsync())!;
	}

	private static async Task<IReadOnlyList<string>> ReadPriorityNamesAsync(DbConnection connection)
	{
		var names = new List<string>();

		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT name FROM priority ORDER BY id;";

		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			names.Add(reader.GetString(0));
		}

		return names;
	}

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}
