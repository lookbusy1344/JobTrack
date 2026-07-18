namespace JobTrack.AdminCli.Tests;

using System.Data.Common;
using Abstractions;
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
///     Thin end-to-end check that <see cref="BootstrapCommand" /> is wired correctly against a real,
///     schema-deployed database — deliberately not re-proving bootstrap semantics, which are already
///     covered by <c>InstallationCommandsTests</c> and <c>ProviderIntegrationTests</c>.
/// </summary>
public sealed class BootstrapCommandIntegrationTests
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "admincli-tests";

	[Fact]
	public async Task Bootstrap_creates_the_first_administrator_on_sqlite()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeployAsync(
				SchemaProvider.Sqlite,
				database.ConnectionString,
				static async connection => {
					await using var command = connection.CreateCommand();
					command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
					_ = await command.ExecuteNonQueryAsync();
				},
				static connection => new SqliteSchemaVersionStore(),
				static connection => new SqliteDeploymentLockStrategy());

			var client = JobTrackSqlite.Create(database.ConnectionString);
			var console = new FakeConsoleIO(
				["Ada Lovelace", "Europe/London", "ada.lovelace"],
				["correct-horse-battery-staple", "correct-horse-battery-staple"]);

			var exitCode = await BootstrapCommand.RunAsync(console, client.Installation, "os-user", CancellationToken.None);

			exitCode.Should().Be(0);
			var accountState = await client.Query.GetAccountStateAsync(new() {
				Context = new() { Actor = new(1), CorrelationId = Guid.NewGuid() },
				TargetUserId = new(1),
			});
			accountState.UserName.Should().Be("ada.lovelace");
			accountState.Roles.Should().Contain(EmployeeRole.Administrator);
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Bootstrap_creates_the_first_administrator_on_postgresql()
	{
		var database = new PostgreSqlDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeployAsync(
				SchemaProvider.PostgreSql,
				database.ConnectionString,
				static connection => Task.CompletedTask,
				static connection => new PostgreSqlSchemaVersionStore(),
				static connection => new PostgreSqlDeploymentLockStrategy());

			await using var dataSource = new NpgsqlDataSourceBuilder(database.ConnectionString).UseNodaTime().Build();
			var client = JobTrackPostgreSql.Create(dataSource);
			var console = new FakeConsoleIO(
				["Ada Lovelace", "Europe/London", "ada.lovelace"],
				["correct-horse-battery-staple", "correct-horse-battery-staple"]);

			var exitCode = await BootstrapCommand.RunAsync(console, client.Installation, "os-user", CancellationToken.None);

			exitCode.Should().Be(0);
			var accountState = await client.Query.GetAccountStateAsync(new() {
				Context = new() { Actor = new(1), CorrelationId = Guid.NewGuid() },
				TargetUserId = new(1),
			});
			accountState.UserName.Should().Be("ada.lovelace");
			accountState.Roles.Should().Contain(EmployeeRole.Administrator);
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Bootstrap_clears_the_forced_password_change_when_requested_on_sqlite()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeployAsync(
				SchemaProvider.Sqlite,
				database.ConnectionString,
				static async connection => {
					await using var command = connection.CreateCommand();
					command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
					_ = await command.ExecuteNonQueryAsync();
				},
				static connection => new SqliteSchemaVersionStore(),
				static connection => new SqliteDeploymentLockStrategy());

			var services = new ServiceCollection();
			_ = services.AddLogging();
			_ = services.AddJobTrackIdentitySqlite(database.ConnectionString);
			await using var provider = services.BuildServiceProvider();
			using var scope = provider.CreateScope();
			var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();

			var client = JobTrackSqlite.Create(database.ConnectionString);
			var console = new FakeConsoleIO(
				["Ada Lovelace", "Europe/London", "ada.lovelace"],
				["correct-horse-battery-staple", "correct-horse-battery-staple"]);

			var exitCode = await BootstrapCommand.RunAsync(
				console, client.Installation, "os-user", CancellationToken.None,
				null, userManager, false);

			exitCode.Should().Be(0);
			var accountState = await client.Query.GetAccountStateAsync(new() {
				Context = new() { Actor = new(1), CorrelationId = Guid.NewGuid() },
				TargetUserId = new(1),
			});
			accountState.UserName.Should().Be("ada.lovelace");
			accountState.RequiresPasswordChange.Should().BeFalse();
		}
		finally {
			await database.DisposeAsync();
		}
	}

	private static async Task DeployAsync(
		SchemaProvider provider,
		string connectionString,
		Func<DbConnection, Task> prepareConnectionAsync,
		Func<DbConnection, ISchemaVersionStore> createStore,
		Func<DbConnection, IDeploymentLockStrategy> createLockStrategy)
	{
		DbConnection connection = provider switch {
			SchemaProvider.PostgreSql => new NpgsqlConnection(connectionString),
			SchemaProvider.Sqlite => new SqliteConnection(connectionString),
			_ => throw new ArgumentOutOfRangeException(nameof(provider)),
		};
		await using var ownedConnection = connection;
		await connection.OpenAsync();
		await prepareConnectionAsync(connection);

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(provider));
		var deployer = new SchemaDeployer(connection, createStore(connection), createLockStrategy(connection), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}
}
