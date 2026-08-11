namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Shared TC-DB-RATE-001 contract for schema slice 11: effective-dated
///     <c>user_cost_rate</c> rows and inherited <c>node_rate_override</c> rows
///     (impl plan §6.2 item 11, spec §9.1/§9.2), asserted identically against
///     PostgreSQL and SQLite by <see cref="PostgreSqlRateSchemaTests" /> and
///     <see cref="SqliteRateSchemaTests" />. Rate precedence (spec §9.3) and the
///     nearest-ancestor override search (spec §9.2) are Application/Domain-layer
///     query concerns, out of scope here.
/// </summary>
public abstract class RateSchemaContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const short PriorityMedium = 2;

	private static readonly DateTimeOffset Epoch = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

	private readonly IDisposableTestDatabase database;

	protected RateSchemaContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Deploying_creates_empty_rate_tables()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		(await CountRowsAsync(connection, "user_cost_rate")).Should().Be(0);
		(await CountRowsAsync(connection, "node_rate_override")).Should().Be(0);
	}

	[Fact]
	public async Task Inserting_an_open_ended_user_cost_rate_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");

		var id = await InsertUserCostRateAsync(connection, userId, Epoch, null, 25.00m);

		id.Should().BePositive();
		await AssertUserCostRateRangeAsync(connection, id, Epoch, null);
	}

	[Fact]
	public async Task Inserting_a_user_cost_rate_ending_at_or_before_it_starts_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");

		var act = async () => await InsertUserCostRateAsync(connection, userId, Epoch, Epoch, 25.00m);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_a_negative_user_cost_rate_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");

		var act = async () => await InsertUserCostRateAsync(connection, userId, Epoch, null, -1.00m);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Overlapping_user_cost_rates_for_the_same_user_are_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertUserCostRateAsync(connection, userId, Epoch, Epoch.AddDays(30), 25.00m);

		var act = async () => await InsertUserCostRateAsync(connection, userId, Epoch.AddDays(15), null, 30.00m);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Adjacent_user_cost_rates_for_the_same_user_succeed()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var firstId = await InsertUserCostRateAsync(connection, userId, Epoch, Epoch.AddDays(30), 25.00m);

		var id = await InsertUserCostRateAsync(connection, userId, Epoch.AddDays(30), null, 30.00m);

		id.Should().BePositive();
		await AssertUserCostRateRangeAsync(connection, firstId, Epoch, Epoch.AddDays(30));
		await AssertUserCostRateRangeAsync(connection, id, Epoch.AddDays(30), null);
	}

	[Fact]
	public async Task Overlapping_user_cost_rates_for_different_users_succeed()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (firstUserId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var (secondUserId, _) = await SeedAppUserAsync(connection, "Bob Example");
		await InsertUserCostRateAsync(connection, firstUserId, Epoch, null, 25.00m);

		var id = await InsertUserCostRateAsync(connection, secondUserId, Epoch, null, 30.00m);

		id.Should().BePositive();
	}

	[Fact]
	public async Task Inserting_a_node_rate_override_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var nodeId = await InsertRootNodeAsync(connection, userId);

		var id = await InsertNodeRateOverrideAsync(connection, nodeId, userId, Epoch, null, 40.00m);

		id.Should().BePositive();
		await AssertNodeRateOverrideRangeAsync(connection, id, Epoch, null);
	}

	[Fact]
	public async Task Overlapping_node_rate_overrides_for_the_same_node_and_user_are_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var nodeId = await InsertRootNodeAsync(connection, userId);
		await InsertNodeRateOverrideAsync(connection, nodeId, userId, Epoch, Epoch.AddDays(30), 40.00m);

		var act = async () => await InsertNodeRateOverrideAsync(connection, nodeId, userId, Epoch.AddDays(15), null, 45.00m);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Adjacent_node_rate_overrides_for_the_same_node_and_user_succeed()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var nodeId = await InsertRootNodeAsync(connection, userId);
		var firstId = await InsertNodeRateOverrideAsync(connection, nodeId, userId, Epoch, Epoch.AddDays(30), 40.00m);

		var id = await InsertNodeRateOverrideAsync(connection, nodeId, userId, Epoch.AddDays(30), null, 45.00m);

		id.Should().BePositive();
		await AssertNodeRateOverrideRangeAsync(connection, firstId, Epoch, Epoch.AddDays(30));
		await AssertNodeRateOverrideRangeAsync(connection, id, Epoch.AddDays(30), null);
	}

	[Fact]
	public async Task Overlapping_node_rate_overrides_for_the_same_node_but_different_users_succeed()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (firstUserId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var (secondUserId, _) = await SeedAppUserAsync(connection, "Bob Example");
		var nodeId = await InsertRootNodeAsync(connection, firstUserId);
		await InsertNodeRateOverrideAsync(connection, nodeId, firstUserId, Epoch, null, 40.00m);

		var id = await InsertNodeRateOverrideAsync(connection, nodeId, secondUserId, Epoch, null, 45.00m);

		id.Should().BePositive();
	}

	[Fact]
	public async Task Overlapping_node_rate_overrides_for_the_same_user_but_different_nodes_succeed()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var firstNodeId = await InsertRootNodeAsync(connection, userId);
		var secondNodeId = await InsertChildNodeAsync(connection, userId, firstNodeId);
		await InsertNodeRateOverrideAsync(connection, firstNodeId, userId, Epoch, null, 40.00m);

		var id = await InsertNodeRateOverrideAsync(connection, secondNodeId, userId, Epoch, null, 45.00m);

		id.Should().BePositive();
	}

	[Fact]
	public async Task Concurrent_overlapping_user_cost_rates_for_the_same_user_allow_exactly_one_to_succeed()
	{
		await using var seedConnection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(seedConnection, "Alice Example");

		await using var connectionA = await OpenExistingConnectionAsync();
		await using var connectionB = await OpenExistingConnectionAsync();

		var results = await Task.WhenAll(
			TryInsertUserCostRateAsync(connectionA, userId, Epoch, Epoch.AddDays(30), 25.00m),
			TryInsertUserCostRateAsync(connectionB, userId, Epoch.AddDays(15), null, 30.00m));

		results.Count(succeeded => succeeded).Should().Be(1);
	}

	[Fact]
	public async Task Concurrent_overlapping_node_rate_overrides_for_the_same_node_and_user_allow_exactly_one_to_succeed()
	{
		await using var seedConnection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(seedConnection, "Alice Example");
		var nodeId = await InsertRootNodeAsync(seedConnection, userId);

		await using var connectionA = await OpenExistingConnectionAsync();
		await using var connectionB = await OpenExistingConnectionAsync();

		var results = await Task.WhenAll(
			TryInsertNodeRateOverrideAsync(connectionA, nodeId, userId, Epoch, Epoch.AddDays(30), 40.00m),
			TryInsertNodeRateOverrideAsync(connectionB, nodeId, userId, Epoch.AddDays(15), null, 45.00m));

		results.Count(succeeded => succeeded).Should().Be(1);
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	/// <summary>PostgreSQL binds <see cref="DateTimeOffset" /> directly; SQLite needs ADR 0007's unix-epoch-ticks encoding.</summary>
	protected abstract object EncodeInstant(DateTimeOffset value);

	/// <summary>PostgreSQL binds <see cref="decimal" /> to <c>numeric</c> directly; SQLite uses a canonical fixed-point string (ADR 0009).</summary>
	protected abstract object EncodeRate(decimal value);

	/// <summary>
	///     Drift check for the generated <c>user_cost_rate.effective_range</c> column (remediation plan
	///     §3.1): a no-op on providers with no such column. PostgreSQL overrides this to read the
	///     stored range back and assert it matches <paramref name="effectiveStart" />/<paramref name="effectiveEnd" />.
	/// </summary>
	protected virtual Task AssertUserCostRateRangeAsync(
		DbConnection connection, long userCostRateId, DateTimeOffset effectiveStart, DateTimeOffset? effectiveEnd) =>
		Task.CompletedTask;

	/// <summary>
	///     Drift check for the generated <c>node_rate_override.effective_range</c> column
	///     (remediation plan §3.1): a no-op on providers with no such column. PostgreSQL overrides this
	///     to read the stored range back and assert it matches
	///     <paramref name="effectiveStart" />/<paramref name="effectiveEnd" />.
	/// </summary>
	protected virtual Task AssertNodeRateOverrideRangeAsync(
		DbConnection connection, long nodeRateOverrideId, DateTimeOffset effectiveStart, DateTimeOffset? effectiveEnd) =>
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

	private static async Task<(long UserId, long IdentityUserId)> SeedAppUserAsync(DbConnection connection, string displayName)
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

	private async Task<long> InsertRootNodeAsync(DbConnection connection, long ownerUserId) =>
		await InsertNodeAsync(connection, ownerUserId, null);

	private async Task<long> InsertChildNodeAsync(DbConnection connection, long ownerUserId, long parentId) =>
		await InsertNodeAsync(connection, ownerUserId, parentId);

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

	private async Task<long> InsertUserCostRateAsync(
		DbConnection connection, long userId, DateTimeOffset effectiveStart, DateTimeOffset? effectiveEnd, decimal rate)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO user_cost_rate (user_id, effective_start, effective_end, rate, changed_at)
							  VALUES (@userId, @effectiveStart, @effectiveEnd, @rate, @changedAt)
							  RETURNING id;
							  """;
		AddParameter(command, "@userId", userId);
		AddParameter(command, "@effectiveStart", EncodeInstant(effectiveStart));
		AddParameter(command, "@effectiveEnd", effectiveEnd is null ? DBNull.Value : EncodeInstant(effectiveEnd.Value));
		AddParameter(command, "@rate", EncodeRate(rate));
		AddParameter(command, "@changedAt", EncodeInstant(DateTimeOffset.UtcNow));

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<long> InsertNodeRateOverrideAsync(
		DbConnection connection, long nodeId, long userId, DateTimeOffset effectiveStart, DateTimeOffset? effectiveEnd, decimal rate)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO node_rate_override (node_id, user_id, effective_start, effective_end, rate, changed_at)
							  VALUES (@nodeId, @userId, @effectiveStart, @effectiveEnd, @rate, @changedAt)
							  RETURNING id;
							  """;
		AddParameter(command, "@nodeId", nodeId);
		AddParameter(command, "@userId", userId);
		AddParameter(command, "@effectiveStart", EncodeInstant(effectiveStart));
		AddParameter(command, "@effectiveEnd", effectiveEnd is null ? DBNull.Value : EncodeInstant(effectiveEnd.Value));
		AddParameter(command, "@rate", EncodeRate(rate));
		AddParameter(command, "@changedAt", EncodeInstant(DateTimeOffset.UtcNow));

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<bool> TryInsertUserCostRateAsync(
		DbConnection connection, long userId, DateTimeOffset effectiveStart, DateTimeOffset? effectiveEnd, decimal rate)
	{
		try {
			await InsertUserCostRateAsync(connection, userId, effectiveStart, effectiveEnd, rate);
			return true;
		}
		catch (DbException) {
			return false;
		}
	}

	private async Task<bool> TryInsertNodeRateOverrideAsync(
		DbConnection connection, long nodeId, long userId, DateTimeOffset effectiveStart, DateTimeOffset? effectiveEnd, decimal rate)
	{
		try {
			await InsertNodeRateOverrideAsync(connection, nodeId, userId, effectiveStart, effectiveEnd, rate);
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
