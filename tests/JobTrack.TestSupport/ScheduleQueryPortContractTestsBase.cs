namespace JobTrack.TestSupport;

using System.Data.Common;
using Abstractions;
using Application;
using Application.Ports;
using AwesomeAssertions;
using Database;
using Domain.Schedules;
using NodaTime;

/// <summary>
///     Shared contract for <see cref="IScheduleQueryPort" /> (plan §8.5 slice 6), asserted identically
///     against PostgreSQL and SQLite by one thin sealed subclass per provider's own test project --
///     same shape as <see cref="ScheduleCommandPortContractTestsBase" />. Seeds a schedule version and
///     exception via the real <see cref="IInstallationBootstrapPort" />/<see cref="IScheduleCommandPort" />.
/// </summary>
public abstract class ScheduleQueryPortContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected ScheduleQueryPortContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task GetScheduleAsync_returns_the_employees_versions_and_exceptions()
	{
		var (_, workerId) = await SeedScheduleAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetScheduleAsync(workerId, workerId);

		result.Versions.Should().ContainSingle();
		result.Exceptions.Should().ContainSingle();
	}

	[Fact]
	public async Task GetScheduleAsync_returns_the_actors_current_roles()
	{
		var (administratorId, workerId) = await SeedScheduleAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetScheduleAsync(administratorId, workerId);

		result.ActorRoles.Should().Contain(EmployeeRole.Administrator);
	}

	[Fact]
	public async Task GetScheduleAsync_returns_empty_for_an_employee_with_no_schedule_data()
	{
		var (administratorId, _) = await SeedScheduleAsync();
		var otherWorkerId = await DatabaseContractTestSupport.SeedEmployeeAsync(database, CreateConnection, PrepareConnectionAsync, "Alan Turing", "alan.turing.schedule", EmployeeRole.Worker);
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetScheduleAsync(administratorId, otherWorkerId);

		result.Versions.Should().BeEmpty();
		result.Exceptions.Should().BeEmpty();
	}

	[Fact]
	public async Task GetScheduleAsync_throws_for_a_nonexistent_actor()
	{
		var (administratorId, workerId) = await SeedScheduleAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var act = () => port.GetScheduleAsync(new(administratorId.Value + 999), workerId);

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task GetScheduleAsync_throws_for_a_nonexistent_employee()
	{
		var (administratorId, _) = await SeedScheduleAsync();
		var port = CreateQueryPort(database.ConnectionString);

		var act = () => port.GetScheduleAsync(administratorId, new(administratorId.Value + 999));

		await act.Should().ThrowAsync<EntityNotFoundException>();
	}

	[Fact]
	public async Task A_whole_second_weekly_interval_round_trips_exactly()
	{
		var (administratorId, _) = await SeedScheduleAsync();
		var workerId = await DatabaseContractTestSupport.SeedEmployeeAsync(database, CreateConnection, PrepareConnectionAsync, "Katherine Jones", "katherine.jones.schedule-query", EmployeeRole.Worker);
		var commandPort = CreateCommandPort(database.ConnectionString);
		_ = await commandPort.AddScheduleVersionAsync(new() {
			Context = ContextFor(administratorId),
			UserId = workerId,
			Schedule = new(
				DateTimeZoneProviders.Tzdb["Europe/London"],
				new(2027, 1, 1),
				null,
				[new(IsoDayOfWeek.Tuesday, new(9, 0, 37), new(17, 30, 52))]),
		});
		var port = CreateQueryPort(database.ConnectionString);

		var result = await port.GetScheduleAsync(workerId, workerId);

		var version = result.Versions.Single(v => v.Schedule.EffectiveStart == new LocalDate(2027, 1, 1));
		var interval = version.Schedule.WeeklyIntervals.Single();
		interval.Start.Should().Be(new(9, 0, 37));
		interval.End.Should().Be(new(17, 30, 52));
	}

	[Fact]
	public async Task GetScheduleAsync_throws_a_domain_fault_when_a_stored_zone_id_is_no_longer_recognized()
	{
		var (_, workerId) = await SeedScheduleAsync();
		await CorruptStoredScheduleZoneIdAsync(workerId, "Bogus/NotAZone");
		var port = CreateQueryPort(database.ConnectionString);

		var act = () => port.GetScheduleAsync(workerId, workerId);

		await act.Should().ThrowAsync<UnknownStoredTimeZoneException>();
	}

	private async Task CorruptStoredScheduleZoneIdAsync(AppUserId workerId, string ianaTimeZone)
	{
		await using var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync);
		await using var command = connection.CreateCommand();
		command.CommandText = "UPDATE user_schedule_version SET iana_time_zone = @ianaTimeZone WHERE user_id = @userId;";
		command.AddParameter("@ianaTimeZone", ianaTimeZone);
		command.AddParameter("@userId", workerId.Value);
		_ = await command.ExecuteNonQueryAsync();
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	internal abstract IInstallationBootstrapPort CreateBootstrapPort(string connectionString);

	internal abstract IScheduleCommandPort CreateCommandPort(string connectionString);

	internal abstract IScheduleQueryPort CreateQueryPort(string connectionString);

	private static CommandContext ContextFor(AppUserId actor) => new() {
		Actor = actor,
		CorrelationId = Guid.NewGuid(),
	};

	private async Task<(AppUserId AdministratorId, AppUserId WorkerId)> SeedScheduleAsync()
	{
		await using (var connection = await database.OpenExistingConnectionAsync(CreateConnection, PrepareConnectionAsync)) {
			var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
			var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
			await deployer.DeployAsync(scripts, CancellationToken.None);
		}

		var bootstrapPort = CreateBootstrapPort(database.ConnectionString);
		var bootstrap = await bootstrapPort.BootstrapAsync(new() {
			DisplayName = "Ada Lovelace",
			IanaTimeZone = "Europe/London",
			UserName = "ada.lovelace",
			PasswordHash = "test-hash",
			SecurityStamp = Guid.NewGuid().ToString("N"),
		});
		var administratorId = bootstrap.AdministratorId;

		var workerId = await DatabaseContractTestSupport.SeedEmployeeAsync(database, CreateConnection, PrepareConnectionAsync, "Grace Hopper", "grace.hopper.schedule-query", EmployeeRole.Worker);

		var commandPort = CreateCommandPort(database.ConnectionString);
		_ = await commandPort.AddScheduleVersionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Schedule = new(
				DateTimeZoneProviders.Tzdb["Europe/London"], new(2026, 1, 1), null,
				[new(IsoDayOfWeek.Monday, new(9, 0), new(17, 0))]),
		});
		_ = await commandPort.AddScheduleExceptionAsync(new() {
			Context = ContextFor(workerId),
			UserId = workerId,
			Entry = new(
				ScheduleExceptionEffect.RemoveWorkingTime,
				new(Instant.FromUtc(2026, 1, 5, 0, 0), Instant.FromUtc(2026, 1, 6, 0, 0)),
				null),
			Reason = "Public holiday",
		});

		return (administratorId, workerId);
	}
}
