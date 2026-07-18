namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Shared TC-DB-SCHED-002 contract for schema slice 10: additive/subtractive
///     <c>user_schedule_exception</c> rows, optional additive-exception rates,
///     and non-overlap of explicitly priced additive exceptions (impl plan
///     §6.2 item 10, spec §8.3), asserted identically against PostgreSQL and
///     SQLite by <see cref="PostgreSqlScheduleExceptionSchemaTests" /> and
///     <see cref="SqliteScheduleExceptionSchemaTests" />.
/// </summary>
public abstract class ScheduleExceptionSchemaContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const short EffectAddWorkingTime = 1;
	private const short EffectRemoveWorkingTime = 2;

	private static readonly IReadOnlyList<string> SeededEffectNames = ["AddWorkingTime", "RemoveWorkingTime"];
	private static readonly DateTimeOffset Epoch = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

	private readonly IDisposableTestDatabase database;

	protected ScheduleExceptionSchemaContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Deploying_creates_an_empty_table_and_seeded_effects()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		(await CountRowsAsync(connection, "user_schedule_exception")).Should().Be(0);
		(await ReadEffectNamesAsync(connection)).Should().BeEquivalentTo(SeededEffectNames);
	}

	[Fact]
	public async Task Inserting_an_unpriced_additive_exception_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");

		var id = await InsertExceptionAsync(connection, userId, Epoch, Epoch.AddHours(2), EffectAddWorkingTime, null);

		id.Should().BePositive();
		await AssertExceptionRangeAsync(connection, id, Epoch, Epoch.AddHours(2));
	}

	[Fact]
	public async Task Inserting_a_priced_additive_exception_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");

		var id = await InsertExceptionAsync(connection, userId, Epoch, Epoch.AddHours(2), EffectAddWorkingTime, 25.50m);

		id.Should().BePositive();
	}

	[Fact]
	public async Task Inserting_an_unpriced_subtractive_exception_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");

		var id = await InsertExceptionAsync(connection, userId, Epoch, Epoch.AddHours(2), EffectRemoveWorkingTime, null);

		id.Should().BePositive();
	}

	[Fact]
	public async Task Inserting_a_priced_subtractive_exception_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");

		var act = async () => await InsertExceptionAsync(connection, userId, Epoch, Epoch.AddHours(2), EffectRemoveWorkingTime, 25.50m);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_an_exception_finishing_at_or_before_it_started_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");

		var act = async () => await InsertExceptionAsync(connection, userId, Epoch, Epoch, EffectAddWorkingTime, null);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_an_exception_with_a_blank_reason_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");

		var act = async () =>
			await InsertExceptionAsync(connection, userId, Epoch, Epoch.AddHours(1), EffectAddWorkingTime, null, "   ");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Overlapping_priced_additive_exceptions_for_the_same_user_are_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertExceptionAsync(connection, userId, Epoch, Epoch.AddHours(2), EffectAddWorkingTime, 25.50m);

		var act = async () =>
			await InsertExceptionAsync(connection, userId, Epoch.AddHours(1), Epoch.AddHours(3), EffectAddWorkingTime, 30.00m);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Adjacent_priced_additive_exceptions_for_the_same_user_succeed()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertExceptionAsync(connection, userId, Epoch, Epoch.AddHours(2), EffectAddWorkingTime, 25.50m);

		var id = await InsertExceptionAsync(connection, userId, Epoch.AddHours(2), Epoch.AddHours(4), EffectAddWorkingTime, 30.00m);

		id.Should().BePositive();
	}

	[Fact]
	public async Task Overlapping_unpriced_additive_exceptions_for_the_same_user_succeed()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertExceptionAsync(connection, userId, Epoch, Epoch.AddHours(2), EffectAddWorkingTime, null);

		var id = await InsertExceptionAsync(connection, userId, Epoch.AddHours(1), Epoch.AddHours(3), EffectAddWorkingTime, null);

		id.Should().BePositive();
	}

	[Fact]
	public async Task Overlapping_priced_additive_exceptions_for_different_users_succeed()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (firstUserId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var (secondUserId, _) = await SeedAppUserAsync(connection, "Bob Example");
		await InsertExceptionAsync(connection, firstUserId, Epoch, Epoch.AddHours(2), EffectAddWorkingTime, 25.50m);

		var id = await InsertExceptionAsync(connection, secondUserId, Epoch, Epoch.AddHours(2), EffectAddWorkingTime, 30.00m);

		id.Should().BePositive();
	}

	[Fact]
	public async Task An_overlapping_subtractive_exception_alongside_a_priced_additive_exception_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertExceptionAsync(connection, userId, Epoch, Epoch.AddHours(2), EffectAddWorkingTime, 25.50m);

		var id = await InsertExceptionAsync(connection, userId, Epoch, Epoch.AddHours(2), EffectRemoveWorkingTime, null);

		id.Should().BePositive();
	}

	[Fact]
	public async Task Overlapping_subtractive_exceptions_for_the_same_user_succeed()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertExceptionAsync(connection, userId, Epoch, Epoch.AddHours(2), EffectRemoveWorkingTime, null);

		var id = await InsertExceptionAsync(connection, userId, Epoch.AddHours(1), Epoch.AddHours(3), EffectRemoveWorkingTime, null);

		id.Should().BePositive();
	}

	[Fact]
	public async Task Concurrent_overlapping_priced_additive_exceptions_for_the_same_user_allow_exactly_one_to_succeed()
	{
		await using var seedConnection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(seedConnection, "Alice Example");

		await using var connectionA = await OpenExistingConnectionAsync();
		await using var connectionB = await OpenExistingConnectionAsync();

		var results = await Task.WhenAll(
			TryInsertExceptionAsync(connectionA, userId, Epoch, Epoch.AddHours(2), EffectAddWorkingTime, 25.50m),
			TryInsertExceptionAsync(connectionB, userId, Epoch.AddHours(1), Epoch.AddHours(3), EffectAddWorkingTime, 30.00m));

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
	///     Drift check for the generated <c>user_schedule_exception.exception_range</c> column
	///     (remediation plan §3.1): a no-op on providers with no such column. PostgreSQL overrides this
	///     to read the stored range back and assert it matches
	///     <paramref name="startedAt" />/<paramref name="finishedAt" />. Both bounds are always finite
	///     here -- <c>started_at</c>/<c>finished_at</c> are NOT NULL -- so there is no unbounded case to
	///     cover, unlike the effective-dated tables.
	/// </summary>
	protected virtual Task AssertExceptionRangeAsync(DbConnection connection, long exceptionId, DateTimeOffset startedAt,
		DateTimeOffset finishedAt) =>
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

	private async Task<long> InsertExceptionAsync(
		DbConnection connection,
		long userId,
		DateTimeOffset startedAt,
		DateTimeOffset finishedAt,
		short effectId,
		decimal? rateOverride,
		string reason = "Public holiday")
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO user_schedule_exception
							  	(user_id, started_at, finished_at, effect_id, rate_override, reason, created_by, changed_at)
							  VALUES
							  	(@userId, @startedAt, @finishedAt, @effectId, @rateOverride, @reason, @userId, @changedAt)
							  RETURNING id;
							  """;
		AddParameter(command, "@userId", userId);
		AddParameter(command, "@startedAt", EncodeInstant(startedAt));
		AddParameter(command, "@finishedAt", EncodeInstant(finishedAt));
		AddParameter(command, "@effectId", effectId);
		AddParameter(command, "@rateOverride", rateOverride is null ? DBNull.Value : EncodeRate(rateOverride.Value));
		AddParameter(command, "@reason", reason);
		AddParameter(command, "@changedAt", EncodeInstant(DateTimeOffset.UtcNow));

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<bool> TryInsertExceptionAsync(
		DbConnection connection, long userId, DateTimeOffset startedAt, DateTimeOffset finishedAt, short effectId, decimal? rateOverride)
	{
		try {
			await InsertExceptionAsync(connection, userId, startedAt, finishedAt, effectId, rateOverride);
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

	private static async Task<IReadOnlyList<string>> ReadEffectNamesAsync(DbConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT name FROM schedule_exception_effect ORDER BY id;";

		var names = new List<string>();
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
