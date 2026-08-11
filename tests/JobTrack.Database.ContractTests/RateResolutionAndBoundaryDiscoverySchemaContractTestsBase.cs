namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Shared TC-DB-RATE-002 contract for schema slice 13c: rate-resolution
///     precedence and rate-boundary discovery (impl plan §6.2 item 13, §6.5,
///     spec §9.1-§9.3), asserted identically against PostgreSQL and SQLite by
///     <see cref="PostgreSqlRateResolutionAndBoundaryDiscoverySchemaTests" />
///     and <see cref="SqliteRateResolutionAndBoundaryDiscoverySchemaTests" />.
///     Hierarchy/achievement/readiness (13a) and worker overlap discovery
///     (13b) are the sibling sub-slices of item 13; the schedule-dependent
///     effective-working-interval clip (`clip_to_working_set`) is out of scope
///     here -- see the sibling PostgreSQL script's header for why (ADR 0008).
/// </summary>
public abstract class RateResolutionAndBoundaryDiscoverySchemaContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const short PriorityMedium = 2;
	private const short ScheduleExceptionEffectAddWorkingTime = 1;

	private static readonly DateTimeOffset RangeStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
	private static readonly DateTimeOffset RangeEnd = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
	private static readonly DateTimeOffset AtInstant = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

	private readonly IDisposableTestDatabase database;

	protected RateResolutionAndBoundaryDiscoverySchemaContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task With_no_rate_source_at_all_resolution_returns_null()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafId) = await SeedUserAndLeafAsync(connection, null);

		(await ResolveRateAsync(connection, leafId, userId, AtInstant)).Should().BeNull();
	}

	[Fact]
	public async Task With_only_a_default_rate_resolution_returns_the_default_rate()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafId) = await SeedUserAndLeafAsync(connection, 20.00m);

		(await ResolveRateAsync(connection, leafId, userId, AtInstant)).Should().Be(20.00m);
	}

	[Fact]
	public async Task An_effective_user_cost_rate_outranks_the_default_rate()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafId) = await SeedUserAndLeafAsync(connection, 20.00m);
		await InsertUserCostRateAsync(connection, userId, RangeStart, RangeEnd, 25.00m);

		(await ResolveRateAsync(connection, leafId, userId, AtInstant)).Should().Be(25.00m);
	}

	[Fact]
	public async Task A_node_rate_override_on_the_node_itself_outranks_the_user_cost_rate()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafId) = await SeedUserAndLeafAsync(connection, 20.00m);
		await InsertUserCostRateAsync(connection, userId, RangeStart, RangeEnd, 25.00m);
		await InsertNodeRateOverrideAsync(connection, leafId, userId, RangeStart, RangeEnd, 30.00m);

		(await ResolveRateAsync(connection, leafId, userId, AtInstant)).Should().Be(30.00m);
	}

	[Fact]
	public async Task A_node_rate_override_on_an_ancestor_outranks_the_user_cost_rate_when_the_node_itself_has_none()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, branchId, leafId) = await SeedUserAndLeafUnderBranchAsync(connection, 20.00m);
		await InsertUserCostRateAsync(connection, userId, RangeStart, RangeEnd, 25.00m);
		await InsertNodeRateOverrideAsync(connection, branchId, userId, RangeStart, RangeEnd, 35.00m);

		(await ResolveRateAsync(connection, leafId, userId, AtInstant)).Should().Be(35.00m);
	}

	[Fact]
	public async Task A_node_rate_override_on_the_node_itself_outranks_one_on_an_ancestor()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, branchId, leafId) = await SeedUserAndLeafUnderBranchAsync(connection, 20.00m);
		await InsertNodeRateOverrideAsync(connection, branchId, userId, RangeStart, RangeEnd, 35.00m);
		await InsertNodeRateOverrideAsync(connection, leafId, userId, RangeStart, RangeEnd, 40.00m);

		(await ResolveRateAsync(connection, leafId, userId, AtInstant)).Should().Be(40.00m);
	}

	[Fact]
	public async Task A_priced_additive_exception_outranks_a_node_rate_override()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafId) = await SeedUserAndLeafAsync(connection, 20.00m);
		await InsertNodeRateOverrideAsync(connection, leafId, userId, RangeStart, RangeEnd, 30.00m);
		await InsertPricedAdditiveExceptionAsync(connection, userId, AtInstant.AddHours(-1), AtInstant.AddHours(1), 50.00m);

		(await ResolveRateAsync(connection, leafId, userId, AtInstant)).Should().Be(50.00m);
	}

	[Fact]
	public async Task An_instant_outside_every_effective_range_falls_back_to_the_default_rate()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafId) = await SeedUserAndLeafAsync(connection, 20.00m);
		await InsertUserCostRateAsync(connection, userId, RangeStart, AtInstant.AddDays(-1), 25.00m);

		(await ResolveRateAsync(connection, leafId, userId, AtInstant)).Should().Be(20.00m);
	}

	[Fact]
	public async Task Rate_boundaries_include_edges_from_every_source_within_the_window_and_exclude_out_of_window_edges()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, branchId, leafId) = await SeedUserAndLeafUnderBranchAsync(connection, 20.00m);
		var inWindowUserRateEnd = RangeStart.AddDays(10);
		await InsertUserCostRateAsync(connection, userId, RangeStart, inWindowUserRateEnd, 25.00m);
		var overrideStart = RangeStart.AddDays(5);
		await InsertNodeRateOverrideAsync(connection, branchId, userId, overrideStart, RangeEnd, 35.00m);
		var exceptionStart = RangeStart.AddDays(15);
		var exceptionEnd = RangeStart.AddDays(15).AddHours(2);
		await InsertPricedAdditiveExceptionAsync(connection, userId, exceptionStart, exceptionEnd, 50.00m);
		await InsertUserCostRateAsync(connection, userId, RangeEnd.AddYears(1), RangeEnd.AddYears(1).AddDays(1), 99.00m);

		var boundaries = await RateBoundariesAsync(connection, leafId, userId, RangeStart, RangeEnd);

		boundaries.Should().BeEquivalentTo([
			(RangeStart, inWindowUserRateEnd),
			(overrideStart, RangeEnd),
			(exceptionStart, exceptionEnd),
		]);
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	/// <summary>PostgreSQL binds <see cref="DateTimeOffset" /> directly; SQLite needs ADR 0007's unix-epoch-ticks encoding.</summary>
	protected abstract object EncodeInstant(DateTimeOffset value);

	/// <summary>PostgreSQL invokes the <c>resolve_rate</c> stored function; SQLite issues the equivalent raw parameterized query.</summary>
	protected abstract Task<decimal?> ResolveRateAsync(DbConnection connection, long nodeId, long userId, DateTimeOffset at);

	/// <summary>PostgreSQL invokes the <c>user_rate_boundaries</c> stored function; SQLite issues the equivalent raw recursive query.</summary>
	protected abstract Task<IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>> RateBoundariesAsync(
		DbConnection connection, long nodeId, long userId, DateTimeOffset from, DateTimeOffset rangeEnd);

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

	private async Task<(long UserId, long LeafId)> SeedUserAndLeafAsync(DbConnection connection, decimal? defaultHourlyRate)
	{
		var userId = await SeedAppUserAsync(connection, "Alice Example", defaultHourlyRate);
		var rootId = await InsertNodeAsync(connection, userId, null);
		var leafId = await InsertNodeAsync(connection, userId, rootId);
		return (userId, leafId);
	}

	private async Task<(long UserId, long BranchId, long LeafId)> SeedUserAndLeafUnderBranchAsync(
		DbConnection connection, decimal? defaultHourlyRate)
	{
		var userId = await SeedAppUserAsync(connection, "Alice Example", defaultHourlyRate);
		var rootId = await InsertNodeAsync(connection, userId, null);
		var branchId = await InsertNodeAsync(connection, userId, rootId);
		var leafId = await InsertNodeAsync(connection, userId, branchId);
		return (userId, branchId, leafId);
	}

	private static async Task<long> SeedAppUserAsync(DbConnection connection, string displayName, decimal? defaultHourlyRate)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO app_user (display_name, iana_time_zone, default_hourly_rate)
							  VALUES (@displayName, 'Europe/London', @defaultHourlyRate)
							  RETURNING id;
							  """;
		AddParameter(command, "@displayName", displayName);
		AddParameter(command, "@defaultHourlyRate", (object?)defaultHourlyRate ?? DBNull.Value);
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
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

	private async Task InsertUserCostRateAsync(
		DbConnection connection, long userId, DateTimeOffset effectiveStart, DateTimeOffset? effectiveEnd, decimal rate)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO user_cost_rate (user_id, effective_start, effective_end, rate, changed_at)
							  VALUES (@userId, @effectiveStart, @effectiveEnd, @rate, @changedAt);
							  """;
		AddParameter(command, "@userId", userId);
		AddParameter(command, "@effectiveStart", EncodeInstant(effectiveStart));
		AddParameter(command, "@effectiveEnd", effectiveEnd is null ? DBNull.Value : EncodeInstant(effectiveEnd.Value));
		AddParameter(command, "@rate", rate);
		AddParameter(command, "@changedAt", EncodeInstant(DateTimeOffset.UtcNow));
		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task InsertNodeRateOverrideAsync(
		DbConnection connection, long nodeId, long userId, DateTimeOffset effectiveStart, DateTimeOffset? effectiveEnd, decimal rate)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO node_rate_override (node_id, user_id, effective_start, effective_end, rate, changed_at)
							  VALUES (@nodeId, @userId, @effectiveStart, @effectiveEnd, @rate, @changedAt);
							  """;
		AddParameter(command, "@nodeId", nodeId);
		AddParameter(command, "@userId", userId);
		AddParameter(command, "@effectiveStart", EncodeInstant(effectiveStart));
		AddParameter(command, "@effectiveEnd", effectiveEnd is null ? DBNull.Value : EncodeInstant(effectiveEnd.Value));
		AddParameter(command, "@rate", rate);
		AddParameter(command, "@changedAt", EncodeInstant(DateTimeOffset.UtcNow));
		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task InsertPricedAdditiveExceptionAsync(
		DbConnection connection, long userId, DateTimeOffset startedAt, DateTimeOffset finishedAt, decimal rateOverride)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO user_schedule_exception
							  (user_id, started_at, finished_at, effect_id, rate_override, reason, created_by, changed_at)
							  VALUES
							  (@userId, @startedAt, @finishedAt, @effectId, @rateOverride, @reason, @createdBy, @changedAt);
							  """;
		AddParameter(command, "@userId", userId);
		AddParameter(command, "@startedAt", EncodeInstant(startedAt));
		AddParameter(command, "@finishedAt", EncodeInstant(finishedAt));
		AddParameter(command, "@effectId", ScheduleExceptionEffectAddWorkingTime);
		AddParameter(command, "@rateOverride", rateOverride);
		AddParameter(command, "@reason", "Overtime cover");
		AddParameter(command, "@createdBy", userId);
		AddParameter(command, "@changedAt", EncodeInstant(DateTimeOffset.UtcNow));
		_ = await command.ExecuteNonQueryAsync();
	}

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}
