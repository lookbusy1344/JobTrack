namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Shared TC-DB-SCHED-001 contract for schema slice 9: effective-dated
///     <c>user_schedule_version</c> rows and their <c>user_schedule_interval</c>
///     children (impl plan §6.2 item 9, spec §8.1/§8.2), asserted identically
///     against PostgreSQL and SQLite by
///     <see cref="PostgreSqlScheduleVersionSchemaTests" /> and
///     <see cref="SqliteScheduleVersionSchemaTests" />. Schedule exceptions
///     (schema slice 10, spec §8.3) are out of scope here.
/// </summary>
public abstract class ScheduleVersionSchemaContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const int DayOfWeekMonday = 1;
	private const int DayOfWeekSunday = 7;

	private static readonly DateOnly Day1 = new(2026, 1, 5);
	private static readonly DateOnly Day2 = new(2026, 2, 2);
	private static readonly DateOnly Day3 = new(2026, 3, 2);
	private static readonly TimeOnly NineAm = new(9, 0);
	private static readonly TimeOnly FivePm = new(17, 0);

	private readonly IDisposableTestDatabase database;

	protected ScheduleVersionSchemaContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Deploying_creates_empty_schedule_version_and_interval_tables()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		(await CountRowsAsync(connection, "user_schedule_version")).Should().Be(0);
		(await CountRowsAsync(connection, "user_schedule_interval")).Should().Be(0);
	}

	[Fact]
	public async Task Inserting_an_open_ended_schedule_version_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");

		var id = await InsertScheduleVersionAsync(connection, userId, Day1, null);

		id.Should().BePositive();
		await AssertEffectiveRangeAsync(connection, id, Day1, null);
	}

	[Fact]
	public async Task Inserting_a_schedule_version_ending_at_or_before_it_starts_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");

		var act = async () => await InsertScheduleVersionAsync(connection, userId, Day2, Day2);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Overlapping_schedule_versions_for_the_same_user_are_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertScheduleVersionAsync(connection, userId, Day1, Day3);

		var act = async () => await InsertScheduleVersionAsync(connection, userId, Day2, null);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Adjacent_schedule_versions_for_the_same_user_succeed()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var firstId = await InsertScheduleVersionAsync(connection, userId, Day1, Day2);

		var id = await InsertScheduleVersionAsync(connection, userId, Day2, null);

		id.Should().BePositive();
		await AssertEffectiveRangeAsync(connection, firstId, Day1, Day2);
		await AssertEffectiveRangeAsync(connection, id, Day2, null);
	}

	[Fact]
	public async Task Overlapping_schedule_versions_for_different_users_succeed()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (firstUserId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var (secondUserId, _) = await SeedAppUserAsync(connection, "Bob Example");
		await InsertScheduleVersionAsync(connection, firstUserId, Day1, null);

		var id = await InsertScheduleVersionAsync(connection, secondUserId, Day1, null);

		id.Should().BePositive();
	}

	[Fact]
	public async Task Attaching_a_weekly_interval_to_a_schedule_version_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var versionId = await InsertScheduleVersionAsync(connection, userId, Day1, null);

		var id = await InsertWeeklyIntervalAsync(connection, versionId, DayOfWeekMonday, NineAm, FivePm, false);

		id.Should().BePositive();
	}

	[Fact]
	public async Task A_same_day_interval_ending_at_or_before_it_starts_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var versionId = await InsertScheduleVersionAsync(connection, userId, Day1, null);

		var act = async () => await InsertWeeklyIntervalAsync(connection, versionId, DayOfWeekMonday, FivePm, NineAm, false);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task A_cross_midnight_interval_ending_before_it_starts_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var versionId = await InsertScheduleVersionAsync(connection, userId, Day1, null);

		var id = await InsertWeeklyIntervalAsync(connection, versionId, DayOfWeekMonday, new(22, 0), new(6, 0),
			true);

		id.Should().BePositive();
	}

	[Fact]
	public async Task An_out_of_range_day_of_week_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var versionId = await InsertScheduleVersionAsync(connection, userId, Day1, null);

		var act = async () => await InsertWeeklyIntervalAsync(connection, versionId, 8, NineAm, FivePm, false);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Deleting_a_schedule_version_cascades_to_its_weekly_intervals()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var versionId = await InsertScheduleVersionAsync(connection, userId, Day1, null);
		await InsertWeeklyIntervalAsync(connection, versionId, DayOfWeekSunday, NineAm, FivePm, false);

		await DeleteScheduleVersionAsync(connection, versionId);

		(await CountRowsAsync(connection, "user_schedule_interval")).Should().Be(0);
	}

	[Fact]
	public async Task Concurrent_overlapping_schedule_versions_for_the_same_user_allow_exactly_one_to_succeed()
	{
		await using var seedConnection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(seedConnection, "Alice Example");

		await using var connectionA = await OpenExistingConnectionAsync();
		await using var connectionB = await OpenExistingConnectionAsync();

		var results = await Task.WhenAll(
			TryInsertScheduleVersionAsync(connectionA, userId, Day1, Day3),
			TryInsertScheduleVersionAsync(connectionB, userId, Day2, null));

		results.Count(succeeded => succeeded).Should().Be(1);
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	/// <summary>PostgreSQL binds <see cref="DateTimeOffset" /> directly; SQLite needs ADR 0007's unix-epoch-ticks encoding.</summary>
	protected abstract object EncodeInstant(DateTimeOffset value);

	/// <summary>PostgreSQL binds <see cref="DateOnly" /> to <c>date</c> directly; SQLite uses an ISO-8601 date string.</summary>
	protected abstract object EncodeDate(DateOnly value);

	/// <summary>PostgreSQL binds <see cref="TimeOnly" /> to <c>time</c> directly; SQLite uses ticks since midnight.</summary>
	protected abstract object EncodeTimeOfDay(TimeOnly value);

	/// <summary>
	///     Drift check for the generated <c>user_schedule_version.effective_range</c> column
	///     (remediation plan §3.1): a no-op on providers with no such column. PostgreSQL overrides this
	///     to read the stored range back and assert it matches
	///     <paramref name="effectiveStart" />/<paramref name="effectiveEnd" />.
	/// </summary>
	protected virtual Task AssertEffectiveRangeAsync(DbConnection connection, long scheduleVersionId, DateOnly effectiveStart,
		DateOnly? effectiveEnd) =>
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

	private async Task<long> InsertScheduleVersionAsync(DbConnection connection, long userId, DateOnly effectiveStart, DateOnly? effectiveEnd)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO user_schedule_version (user_id, effective_start, effective_end, iana_time_zone, changed_at)
							  VALUES (@userId, @effectiveStart, @effectiveEnd, 'Europe/London', @changedAt)
							  RETURNING id;
							  """;
		AddParameter(command, "@userId", userId);
		AddParameter(command, "@effectiveStart", EncodeDate(effectiveStart));
		AddParameter(command, "@effectiveEnd", effectiveEnd is null ? DBNull.Value : EncodeDate(effectiveEnd.Value));
		AddParameter(command, "@changedAt", EncodeInstant(DateTimeOffset.UtcNow));

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<long> InsertWeeklyIntervalAsync(
		DbConnection connection, long scheduleVersionId, int dayOfWeek, TimeOnly startTime, TimeOnly endTime, bool crossesMidnight)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO user_schedule_interval (schedule_version_id, day_of_week, start_time, end_time, crosses_midnight)
							  VALUES (@scheduleVersionId, @dayOfWeek, @startTime, @endTime, @crossesMidnight)
							  RETURNING id;
							  """;
		AddParameter(command, "@scheduleVersionId", scheduleVersionId);
		AddParameter(command, "@dayOfWeek", dayOfWeek);
		AddParameter(command, "@startTime", EncodeTimeOfDay(startTime));
		AddParameter(command, "@endTime", EncodeTimeOfDay(endTime));
		AddParameter(command, "@crossesMidnight", crossesMidnight);

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static async Task DeleteScheduleVersionAsync(DbConnection connection, long scheduleVersionId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "DELETE FROM user_schedule_version WHERE id = @scheduleVersionId;";
		AddParameter(command, "@scheduleVersionId", scheduleVersionId);

		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task<bool> TryInsertScheduleVersionAsync(DbConnection connection, long userId, DateOnly effectiveStart, DateOnly? effectiveEnd)
	{
		try {
			await InsertScheduleVersionAsync(connection, userId, effectiveStart, effectiveEnd);
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
