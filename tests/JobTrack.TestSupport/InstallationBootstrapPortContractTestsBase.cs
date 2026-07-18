namespace JobTrack.TestSupport;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application.Ports;
using AwesomeAssertions;
using Database;
using NodaTime;
using NodaTime.TimeZones;

/// <summary>
///     Shared contract for <see cref="IInstallationBootstrapPort.BootstrapAsync" /> (impl plan §7.4
///     step 2, ADR 0005/0012/0015), asserted identically against PostgreSQL and SQLite by one thin
///     sealed subclass per provider's own test project. Lives here rather than following
///     <c>JobTrack.Database.ContractTests</c>' single-project base-plus-subclasses shape, because each
///     provider's persistence test project references only its own provider assembly.
/// </summary>
public abstract class InstallationBootstrapPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected InstallationBootstrapPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Bootstrapping_an_empty_database_creates_administrator_root_and_marker()
	{
		await DeploySchemaAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.BootstrapAsync(CreateRequest("ada"));

		result.AdministratorId.Value.Should().BePositive();
		result.AdministratorVersion.Should().Be(1);
		result.RootJobNodeId.Value.Should().BePositive();
		result.RootVersion.Should().Be(1);
	}

	[Fact]
	public async Task Bootstrapping_twice_throws_already_initialised_with_no_partial_writes()
	{
		await DeploySchemaAsync();
		var port = CreatePort(database.ConnectionString);
		_ = await port.BootstrapAsync(CreateRequest("ada"));

		var act = async () => await port.BootstrapAsync(CreateRequest("grace"));

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("installation-already-initialised");
	}

	[Fact]
	public async Task Bootstrapping_persists_the_canonical_administrator_zone_id()
	{
		await DeploySchemaAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.BootstrapAsync(CreateRequest("ada", "Asia/Calcutta"));

		var storedZoneId = await ReadAppUserZoneIdAsync(result.AdministratorId);
		storedZoneId.Should().Be("Asia/Kolkata");
	}

	[Fact]
	public async Task Bootstrapping_without_an_explicit_rate_persists_the_default_rate_and_schedule()
	{
		await DeploySchemaAsync();
		var port = CreatePort(database.ConnectionString);

		var result = await port.BootstrapAsync(CreateRequest("ada"));

		(await GetDefaultHourlyRateAsync(result.AdministratorId)).Should().Be(20m);
		var schedule = await GetOnlyScheduleAsync(result.AdministratorId);
		schedule.EffectiveStart.Should().Be("2020-01-01");
		schedule.EffectiveEnd.Should().BeNull();
		schedule.IanaTimeZone.Should().Be("Europe/London");
		schedule.Intervals.Should().Equal(new ScheduleIntervalSummary(1, "09:00:00", "17:00:00", false),
			new ScheduleIntervalSummary(2, "09:00:00", "17:00:00", false), new ScheduleIntervalSummary(3, "09:00:00", "17:00:00", false),
			new ScheduleIntervalSummary(4, "09:00:00", "17:00:00", false), new ScheduleIntervalSummary(5, "09:00:00", "17:00:00", false));
	}

	[Fact]
	public async Task Bootstrapping_with_an_unrecognized_zone_id_throws()
	{
		await DeploySchemaAsync();
		var port = CreatePort(database.ConnectionString);

		var act = () => port.BootstrapAsync(CreateRequest("ada", "Bogus/NotAZone"));

		await act.Should().ThrowAsync<DateTimeZoneNotFoundException>();
	}

	[Fact]
	public async Task Concurrent_bootstrap_attempts_allow_exactly_one_to_succeed()
	{
		await DeploySchemaAsync();
		var portA = CreatePort(database.ConnectionString);
		var portB = CreatePort(database.ConnectionString);

		var results = await Task.WhenAll(
			TryBootstrapAsync(portA, "ada"),
			TryBootstrapAsync(portB, "grace"));

		results.Count(succeeded => succeeded).Should().Be(1);
	}

	[Fact]
	public async Task Persisted_root_cannot_be_deleted()
	{
		await DeploySchemaAsync();
		var port = CreatePort(database.ConnectionString);
		var result = await port.BootstrapAsync(CreateRequest("ada"));

		await using var connection = await OpenExistingConnectionAsync();

		var act = async () => await ExecuteNonQueryAsync(
			connection, $"DELETE FROM job_node WHERE id = {result.RootJobNodeId.Value};");

		await act.Should().ThrowAsync<DbException>();
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	protected abstract IInstallationBootstrapPort CreatePort(string connectionString);

	private static BootstrapPersistenceRequest CreateRequest(string userNameSuffix, string ianaTimeZone = "Europe/London") => new() {
		DisplayName = "Ada Lovelace",
		IanaTimeZone = ianaTimeZone,
		UserName = $"ada.lovelace.{userNameSuffix}",
		PasswordHash = "test-hash",
		SecurityStamp = Guid.NewGuid().ToString("N"),
	};

	private static async Task<bool> TryBootstrapAsync(IInstallationBootstrapPort port, string userNameSuffix)
	{
		try {
			_ = await port.BootstrapAsync(CreateRequest(userNameSuffix));
			return true;
		}
		catch (InvariantViolationException) {
			return false;
		}
	}

	private async Task DeploySchemaAsync()
	{
		await using var connection = await OpenExistingConnectionAsync();

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
		var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}

	private async Task<string> ReadAppUserZoneIdAsync(AppUserId appUserId)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT iana_time_zone FROM app_user WHERE id = @appUserId;";
		AddParameter(command, "@appUserId", appUserId.Value);

		return (string)(await command.ExecuteScalarAsync())!;
	}

	private async Task<decimal> GetDefaultHourlyRateAsync(AppUserId appUserId)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT default_hourly_rate FROM app_user WHERE id = @appUserId;";
		AddParameter(command, "@appUserId", appUserId.Value);

		var value = await command.ExecuteScalarAsync();
		return value switch {
			decimal amount => amount,
			string amount => decimal.Parse(amount, CultureInfo.InvariantCulture),
			_ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
		};
	}

	private async Task<ScheduleSummary> GetOnlyScheduleAsync(AppUserId appUserId)
	{
		await using var connection = await OpenExistingConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT sv.effective_start, sv.effective_end, sv.iana_time_zone,
							         si.day_of_week, si.start_time, si.end_time, si.crosses_midnight
							  FROM user_schedule_version sv
							  JOIN user_schedule_interval si ON si.schedule_version_id = sv.id
							  WHERE sv.user_id = @appUserId
							  ORDER BY si.day_of_week;
							  """;
		AddParameter(command, "@appUserId", appUserId.Value);

		await using var reader = await command.ExecuteReaderAsync();
		var intervals = new List<ScheduleIntervalSummary>();
		string? effectiveStart = null;
		string? effectiveEnd = null;
		string? ianaTimeZone = null;
		while (await reader.ReadAsync()) {
			effectiveStart ??= NormalizeDate(reader.GetValue(0));
			effectiveEnd ??= reader.IsDBNull(1) ? null : NormalizeDate(reader.GetValue(1));
			ianaTimeZone ??= reader.GetString(2);
			intervals.Add(new(
				Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
				NormalizeTime(reader.GetValue(4)),
				NormalizeTime(reader.GetValue(5)),
				Convert.ToBoolean(reader.GetValue(6), CultureInfo.InvariantCulture)));
		}

		return new(
			effectiveStart ?? throw new InvalidOperationException("No schedule version was found."),
			effectiveEnd,
			ianaTimeZone ?? throw new InvalidOperationException("No schedule version was found."),
			intervals);
	}

	private static string NormalizeDate(object value) => value switch {
		DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
		DateTime dateTime => DateOnly.FromDateTime(dateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
		string text => text,
		_ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
	};

	private static string NormalizeTime(object value) => value switch {
		LocalTime localTime => FormatTime(localTime.Hour, localTime.Minute, localTime.Second),
		TimeOnly timeOnly => FormatTime(timeOnly.Hour, timeOnly.Minute, timeOnly.Second),
		TimeSpan timeSpan => timeSpan.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
		long ticks => LocalTime.FromTicksSinceMidnight(ticks).ToString("HH:mm:ss", null),
		_ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
	};

	private static string FormatTime(int hour, int minute, int second) =>
		$"{hour:D2}:{minute:D2}:{second:D2}";

	private async Task<DbConnection> OpenExistingConnectionAsync()
	{
		var connection = CreateConnection(database.ConnectionString);
		await connection.OpenAsync();
		await PrepareConnectionAsync(connection);
		return connection;
	}

	private static async Task ExecuteNonQueryAsync(DbConnection connection, string commandText)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = commandText;
		_ = await command.ExecuteNonQueryAsync();
	}

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}

	private sealed class ScheduleSummary(
		string effectiveStart,
		string? effectiveEnd,
		string ianaTimeZone,
		IReadOnlyList<ScheduleIntervalSummary> intervals)
	{
		public string EffectiveStart { get; } = effectiveStart;

		public string? EffectiveEnd { get; } = effectiveEnd;

		public string IanaTimeZone { get; } = ianaTimeZone;

		public IReadOnlyList<ScheduleIntervalSummary> Intervals { get; } = intervals;
	}

	private sealed record ScheduleIntervalSummary(int DayOfWeek, string StartTime, string EndTime, bool CrossesMidnight);
}
