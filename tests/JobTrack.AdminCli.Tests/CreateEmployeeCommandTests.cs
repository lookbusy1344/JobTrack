namespace JobTrack.AdminCli.Tests;

using System.Data.Common;
using Abstractions;
using Application;
using AwesomeAssertions;
using Database;
using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Persistence.PostgreSql;
using Persistence.Sqlite;
using TestSupport;

/// <summary>
///     Real, schema-deployed database tests for <see cref="CreateEmployeeCommand" /> — the
///     <c>create-employee</c> CLI command that provisions a normal (non-administrator) employee under
///     an existing administrator actor, granting one or more roles and optionally clearing the ADR 0023
///     forced-password-change so a published/shared demo credential stays usable.
/// </summary>
public sealed class CreateEmployeeCommandTests
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "admincli-tests";
	private const string AdminUsername = "admin";
	private const string DemoUsername = "demo";
	private const string DemoPassword = "demo1234";

	[Fact]
	public async Task Creates_a_normal_employee_with_multiple_roles_on_sqlite()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await using var harness = await HarnessAsync(SchemaProvider.Sqlite, database.ConnectionString);
			var options = DemoOptions(AdminCliProvider.Sqlite, database.ConnectionString);

			var console = new FakeConsoleIO([], []);
			var exitCode = await CreateEmployeeCommand.RunAsync(console, harness.UserManager, harness.Client, options, CancellationToken.None);

			exitCode.Should().Be(0);
			console.Errors.Should().BeEmpty();

			var state = await AccountStateAsync(harness, DemoUsername);
			state.UserName.Should().Be(DemoUsername);
			state.IsEnabled.Should().BeTrue();
			state.Roles.Should().BeEquivalentTo([EmployeeRole.JobManager, EmployeeRole.Worker]);
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Creates_a_normal_employee_with_multiple_roles_on_postgresql()
	{
		var database = new PostgreSqlDatabaseFixture();
		await database.InitializeAsync();

		try {
			await using var harness = await HarnessAsync(SchemaProvider.PostgreSql, database.ConnectionString);
			var options = DemoOptions(AdminCliProvider.PostgreSql, database.ConnectionString);

			var console = new FakeConsoleIO([], []);
			var exitCode = await CreateEmployeeCommand.RunAsync(console, harness.UserManager, harness.Client, options, CancellationToken.None);

			exitCode.Should().Be(0);
			console.Errors.Should().BeEmpty();

			var state = await AccountStateAsync(harness, DemoUsername);
			state.Roles.Should().BeEquivalentTo([EmployeeRole.JobManager, EmployeeRole.Worker]);
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Clears_the_forced_password_change_when_flagged_on_sqlite()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await using var harness = await HarnessAsync(SchemaProvider.Sqlite, database.ConnectionString);
			var options = DemoOptions(AdminCliProvider.Sqlite, database.ConnectionString) with { ForcePasswordChange = false };

			var console = new FakeConsoleIO([], []);
			var exitCode = await CreateEmployeeCommand.RunAsync(console, harness.UserManager, harness.Client, options, CancellationToken.None);

			exitCode.Should().Be(0);
			var state = await AccountStateAsync(harness, DemoUsername);
			state.RequiresPasswordChange.Should().BeFalse();
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Leaves_the_forced_password_change_set_by_default_on_sqlite()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await using var harness = await HarnessAsync(SchemaProvider.Sqlite, database.ConnectionString);
			var options = DemoOptions(AdminCliProvider.Sqlite, database.ConnectionString);

			var console = new FakeConsoleIO([], []);
			var exitCode = await CreateEmployeeCommand.RunAsync(console, harness.UserManager, harness.Client, options, CancellationToken.None);

			exitCode.Should().Be(0);
			var state = await AccountStateAsync(harness, DemoUsername);
			state.RequiresPasswordChange.Should().BeTrue();
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Fails_for_an_unknown_actor_on_sqlite()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await using var harness = await HarnessAsync(SchemaProvider.Sqlite, database.ConnectionString);
			var options = DemoOptions(AdminCliProvider.Sqlite, database.ConnectionString) with { ActorUsername = "ghost" };

			var console = new FakeConsoleIO([], []);
			var exitCode = await CreateEmployeeCommand.RunAsync(console, harness.UserManager, harness.Client, options, CancellationToken.None);

			exitCode.Should().Be(1);
			console.Errors.Should().ContainSingle(error => error.Contains("ghost", StringComparison.Ordinal));
			(await harness.UserManager.FindByNameAsync(DemoUsername)).Should().BeNull();
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Fails_when_the_actor_is_not_an_administrator_on_sqlite()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await using var harness = await HarnessAsync(SchemaProvider.Sqlite, database.ConnectionString);

			// First create a normal (non-admin) employee, then try to have it create another.
			var demo = DemoOptions(AdminCliProvider.Sqlite, database.ConnectionString);
			_ = await CreateEmployeeCommand.RunAsync(new FakeConsoleIO([], []), harness.UserManager, harness.Client, demo, CancellationToken.None);

			var second = DemoOptions(AdminCliProvider.Sqlite, database.ConnectionString) with {
				ActorUsername = DemoUsername,
				Username = "intruder",
			};
			var console = new FakeConsoleIO([], []);
			var exitCode = await CreateEmployeeCommand.RunAsync(console, harness.UserManager, harness.Client, second, CancellationToken.None);

			exitCode.Should().Be(1);
			console.Errors.Should().ContainSingle();
			(await harness.UserManager.FindByNameAsync("intruder")).Should().BeNull();
		}
		finally {
			await database.DisposeAsync();
		}
	}

	private static CreateEmployeeCommandOptions DemoOptions(AdminCliProvider provider, string connectionString) =>
		new() {
			Provider = provider,
			ConnectionString = connectionString,
			ActorUsername = AdminUsername,
			Username = DemoUsername,
			Password = DemoPassword,
			DisplayName = "Demo User",
			IanaTimeZone = "Europe/London",
			Roles = [EmployeeRole.JobManager, EmployeeRole.Worker],
			DefaultHourlyRate = null,
			ForcePasswordChange = true,
		};

	private static async Task<AccountStateResult> AccountStateAsync(Harness harness, string username)
	{
		var user = await harness.UserManager.FindByNameAsync(username);
		user.Should().NotBeNull();
		var adminUser = await harness.UserManager.FindByNameAsync(AdminUsername);
		return await harness.Client.Query.GetAccountStateAsync(new() {
			Context = new() { Actor = adminUser!.AppUserId, CorrelationId = Guid.NewGuid() },
			TargetUserId = user!.AppUserId,
		});
	}

	private static async Task<Harness> HarnessAsync(SchemaProvider provider, string connectionString)
	{
		await DeploySchemaAsync(provider, connectionString);

		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = provider == SchemaProvider.PostgreSql
			? services.AddJobTrackIdentityPostgreSql(connectionString)
			: services.AddJobTrackIdentitySqlite(connectionString);
		var serviceProvider = services.BuildServiceProvider();
		var scope = serviceProvider.CreateScope();
		var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();

		IJobTrackClient client;
		NpgsqlDataSource? dataSource = null;
		if (provider == SchemaProvider.PostgreSql) {
			dataSource = new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build();
			client = JobTrackPostgreSql.Create(dataSource);
		} else {
			client = JobTrackSqlite.Create(connectionString);
		}

		_ = await client.Installation.BootstrapAdministratorAsync(
			new() {
				DisplayName = "Administrator",
				IanaTimeZone = "Europe/London",
				DefaultHourlyRate = new HourlyRate(20m),
				UserName = AdminUsername,
				Password = "correct-horse-battery-staple",
				CorrelationId = Guid.NewGuid(),
			},
			CancellationToken.None);

		return new(serviceProvider, scope, userManager, client, dataSource);
	}

	private static async Task DeploySchemaAsync(SchemaProvider provider, string connectionString)
	{
		DbConnection connection = provider switch {
			SchemaProvider.PostgreSql => new NpgsqlConnection(connectionString),
			SchemaProvider.Sqlite => new SqliteConnection(connectionString),
			_ => throw new ArgumentOutOfRangeException(nameof(provider)),
		};
		await using var ownedConnection = connection;
		await connection.OpenAsync();

		if (provider == SchemaProvider.Sqlite) {
			await using var pragma = connection.CreateCommand();
			pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
			_ = await pragma.ExecuteNonQueryAsync();
		}

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(provider));
		var deployer = new SchemaDeployer(
			connection,
			provider == SchemaProvider.PostgreSql ? new PostgreSqlSchemaVersionStore() : new SqliteSchemaVersionStore(),
			provider == SchemaProvider.PostgreSql ? new PostgreSqlDeploymentLockStrategy() : new SqliteDeploymentLockStrategy(),
			ApplicationVersion,
			AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}

	private sealed class Harness(
		ServiceProvider serviceProvider,
		IServiceScope scope,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient client,
		NpgsqlDataSource? dataSource) : IAsyncDisposable
	{
		public UserManager<JobTrackIdentityUser> UserManager { get; } = userManager;

		public IJobTrackClient Client { get; } = client;

		public async ValueTask DisposeAsync()
		{
			scope.Dispose();
			await serviceProvider.DisposeAsync();
			if (dataSource is not null) {
				await dataSource.DisposeAsync();
			}
		}
	}
}
