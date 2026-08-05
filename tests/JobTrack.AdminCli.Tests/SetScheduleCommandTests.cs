namespace JobTrack.AdminCli.Tests;

using Application;
using AwesomeAssertions;
using Database;
using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Persistence.Sqlite;
using TestSupport;

/// <summary>
///     Real, schema-deployed database tests for <see cref="SetScheduleCommand" /> — the
///     <c>set-schedule</c> CLI command an installation uses to give an account its standing rota.
///     The case that shapes every test here: an account is never schedule-less. Both <c>bootstrap</c>
///     and <c>create-employee</c> seed <c>EmployeeProvisioningDefaults</c>' Mon–Fri 09:00–17:00 from
///     2020-01-01, open-ended, so this command has to correct that placeholder in place rather than
///     add beside it — a plain add always collides on <c>schedule-version-overlap</c>.
/// </summary>
public sealed class SetScheduleCommandTests
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "admincli-tests";
	private const string KnownPassword = "correct-horse-battery-staple";
	private const string AdminUserName = "ada.admin";

	/// <summary><c>EmployeeProvisioningDefaults.ScheduleEffectiveStart</c>, which provisioning seeds.</summary>
	private static readonly LocalDate ProvisionedEffectiveStart = new(2020, 1, 1);

	[Fact]
	public async Task Replaces_the_provisioned_rota_rather_than_adding_beside_it() =>
		await RunWithDatabaseAsync(async (userManager, client, clock) => {
			var console = new FakeConsoleIO([], []);

			var exitCode = await SetScheduleCommand.RunAsync(
				console, userManager, client,
				Options(AdminUserName, "Mon,Tue,Wed,Thu,Fri,Sat,Sun", "08:00", "20:00"),
				clock, CancellationToken.None);

			console.Errors.Should().BeEmpty();
			exitCode.Should().Be(0);

			var snapshot = await ReadScheduleAsync(client);
			snapshot.Versions.Should().ContainSingle("the provisioned version is corrected, not supplemented");
			var intervals = snapshot.Versions.Single().Schedule.WeeklyIntervals;
			intervals.Should().HaveCount(7);
			intervals.Should().AllSatisfy(interval => {
				interval.Start.Should().Be(new(8, 0));
				interval.End.Should().Be(new(20, 0));
			});
		});

	[Fact]
	public async Task Sets_a_weekday_pattern_covering_each_named_day_in_order() =>
		await RunWithDatabaseAsync(async (userManager, client, clock) => {
			var console = new FakeConsoleIO([], []);

			var exitCode = await SetScheduleCommand.RunAsync(
				console, userManager, client,
				Options(AdminUserName, "Mon,Tue,Wed,Thu,Fri", "09:00", "17:00"),
				clock, CancellationToken.None);

			exitCode.Should().Be(0);
			var intervals = (await ReadScheduleAsync(client)).Versions.Single().Schedule.WeeklyIntervals;
			intervals.Select(interval => interval.Day).Should().Equal(
				IsoDayOfWeek.Monday, IsoDayOfWeek.Tuesday, IsoDayOfWeek.Wednesday, IsoDayOfWeek.Thursday, IsoDayOfWeek.Friday);
		});

	[Fact]
	public async Task Keeps_the_provisioned_effective_start_so_existing_work_stays_covered() =>
		await RunWithDatabaseAsync(async (userManager, client, clock) => {
			var console = new FakeConsoleIO([], []);

			_ = await SetScheduleCommand.RunAsync(
				console, userManager, client,
				Options(AdminUserName, "Mon", "08:00", "20:00"),
				clock, CancellationToken.None);

			(await ReadScheduleAsync(client)).Versions.Single().Schedule.EffectiveStart
				.Should().Be(ProvisionedEffectiveStart);
		});

	[Fact]
	public async Task Honours_an_explicit_effective_start() =>
		await RunWithDatabaseAsync(async (userManager, client, clock) => {
			var console = new FakeConsoleIO([], []);

			var exitCode = await SetScheduleCommand.RunAsync(
				console, userManager, client,
				Options(AdminUserName, "Mon", "09:00", "17:00") with { EffectiveStart = new LocalDate(2026, 6, 1) },
				clock, CancellationToken.None);

			exitCode.Should().Be(0);
			(await ReadScheduleAsync(client)).Versions.Single().Schedule.EffectiveStart
				.Should().Be(new(2026, 6, 1));
		});

	[Fact]
	public async Task Applies_an_explicit_time_zone() =>
		await RunWithDatabaseAsync(async (userManager, client, clock) => {
			var console = new FakeConsoleIO([], []);

			var exitCode = await SetScheduleCommand.RunAsync(
				console, userManager, client,
				Options(AdminUserName, "Mon", "09:00", "17:00", "America/New_York"),
				clock, CancellationToken.None);

			exitCode.Should().Be(0);
			(await ReadScheduleAsync(client)).Versions.Single().Schedule.Zone.Id.Should().Be("America/New_York");
		});

	[Fact]
	public async Task Refuses_when_more_than_one_version_already_exists() =>
		await RunWithDatabaseAsync(async (userManager, client, clock) => {
			await AddASecondVersionAsync(client);
			var console = new FakeConsoleIO([], []);

			var exitCode = await SetScheduleCommand.RunAsync(
				console, userManager, client,
				Options(AdminUserName, "Mon", "08:00", "20:00"),
				clock, CancellationToken.None);

			exitCode.Should().Be(1);
			console.Errors.Should().ContainSingle(error => error.Contains("Rota pages", StringComparison.Ordinal));
			(await ReadScheduleAsync(client)).Versions.Should().HaveCount(2, "nothing was overwritten");
		});

	[Fact]
	public async Task Fails_for_an_unknown_target_username() =>
		await RunWithDatabaseAsync(async (userManager, client, clock) => {
			var console = new FakeConsoleIO([], []);

			var exitCode = await SetScheduleCommand.RunAsync(
				console, userManager, client,
				Options("no.such.user", "Mon", "09:00", "17:00"),
				clock, CancellationToken.None);

			exitCode.Should().Be(1);
			console.Errors.Should().ContainSingle(error => error.Contains("no.such.user", StringComparison.Ordinal));
			console.Lines.Should().BeEmpty();
		});

	[Fact]
	public async Task Fails_for_an_unknown_actor_username() =>
		await RunWithDatabaseAsync(async (userManager, client, clock) => {
			var console = new FakeConsoleIO([], []);

			var exitCode = await SetScheduleCommand.RunAsync(
				console, userManager, client,
				Options(AdminUserName, "Mon", "09:00", "17:00") with { ActorUsername = "no.such.admin" },
				clock, CancellationToken.None);

			exitCode.Should().Be(1);
			console.Errors.Should().ContainSingle(error => error.Contains("no.such.admin", StringComparison.Ordinal));
		});

	[Fact]
	public async Task Fails_for_an_unrecognised_time_zone() =>
		await RunWithDatabaseAsync(async (userManager, client, clock) => {
			var console = new FakeConsoleIO([], []);

			var exitCode = await SetScheduleCommand.RunAsync(
				console, userManager, client,
				Options(AdminUserName, "Mon", "09:00", "17:00", "Mars/Olympus_Mons"),
				clock, CancellationToken.None);

			exitCode.Should().Be(1);
			console.Errors.Should().ContainSingle(error => error.Contains("Mars/Olympus_Mons", StringComparison.Ordinal));
		});

	private static SetScheduleCommandOptions Options(
		string username, string days, string start, string end, string ianaTimeZone = "Europe/London") =>
		SetScheduleCommandOptions.Parse(new([
			"--provider", "sqlite", "--connection-string", "Data Source=unused.db",
			"--actor", AdminUserName, "--username", username,
			"--days", days, "--start", start, "--end", end, "--iana-time-zone", ianaTimeZone,
		]));

	private static CommandContext Context() => new() { Actor = new(1), CorrelationId = Guid.NewGuid() };

	private static async Task<ScheduleSnapshotResult> ReadScheduleAsync(IJobTrackClient client) =>
		await client.Query.GetScheduleAsync(new() { Context = Context(), UserId = new(1) });

	/// <summary>
	///     Closes the provisioned open-ended version and adds a second one after it, so the account has
	///     the real history <see cref="SetScheduleCommand" /> must refuse to overwrite.
	/// </summary>
	private static async Task AddASecondVersionAsync(IJobTrackClient client)
	{
		var boundary = new LocalDate(2030, 1, 1);
		var provisioned = (await ReadScheduleAsync(client)).Versions.Single();

		_ = await client.Schedules.CorrectScheduleVersionAsync(new() {
			Context = Context(),
			VersionId = provisioned.Id,
			Version = provisioned.Version,
			Reason = "Closing the provisioned version for this test.",
			Schedule = new(
				provisioned.Schedule.Zone, provisioned.Schedule.EffectiveStart, boundary, provisioned.Schedule.WeeklyIntervals),
		});

		_ = await client.Schedules.AddScheduleVersionAsync(new() {
			Context = Context(),
			UserId = new(1),
			Schedule = new(
				provisioned.Schedule.Zone, boundary, null, [new(IsoDayOfWeek.Monday, new(10, 0), new(16, 0))]),
		});
	}

	/// <summary>
	///     Runs <paramref name="body" /> against a freshly deployed SQLite database bootstrapped with
	///     <see cref="AdminUserName" /> as its administrator (and so <c>app_user</c> id 1).
	/// </summary>
	private static async Task RunWithDatabaseAsync(
		Func<UserManager<JobTrackIdentityUser>, IJobTrackClient, IClock, Task> body)
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(database.ConnectionString);

			var services = new ServiceCollection();
			_ = services.AddLogging();
			_ = services.AddJobTrackIdentitySqlite(database.ConnectionString);
			await using var provider = services.BuildServiceProvider();
			using var scope = provider.CreateScope();
			var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
			var client = JobTrackSqlite.Create(database.ConnectionString);

			_ = await client.Installation.BootstrapAdministratorAsync(
				new() {
					DisplayName = "Ada Admin",
					IanaTimeZone = "Europe/London",
					UserName = AdminUserName,
					Password = KnownPassword,
					CorrelationId = Guid.NewGuid(),
				},
				CancellationToken.None);

			await body(userManager, client, new FixedClock(Instant.FromUtc(2026, 3, 2, 12, 0)));
		}
		finally {
			await database.DisposeAsync();
		}
	}

	private static async Task DeploySchemaAsync(string connectionString)
	{
		await using var connection = new SqliteConnection(connectionString);
		await connection.OpenAsync();

		await using (var pragma = connection.CreateCommand()) {
			pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
			_ = await pragma.ExecuteNonQueryAsync();
		}

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.Sqlite));
		var deployer = new SchemaDeployer(
			connection, new SqliteSchemaVersionStore(), new SqliteDeploymentLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}
}
