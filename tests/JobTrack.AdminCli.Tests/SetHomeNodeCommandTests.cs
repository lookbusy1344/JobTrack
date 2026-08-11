namespace JobTrack.AdminCli.Tests;

using Abstractions;
using Application;
using AwesomeAssertions;
using Database;
using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Persistence.Sqlite;
using TestSupport;

/// <summary>
///     Real, schema-deployed database tests for <see cref="SetHomeNodeCommand" /> — the
///     <c>set-home-node</c> CLI command that points an existing employee's post-login landing node at a
///     branch, or clears it back to the tree root. The command runs as the named employee themselves
///     (<see cref="IEmployeeCommands.SetHomeNodeAsync" /> is self-service only), so there is no actor
///     flag to test.
/// </summary>
public sealed class SetHomeNodeCommandTests
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "admincli-tests";
	private const string KnownPassword = "correct-horse-battery-staple";

	[Fact]
	public async Task Sets_a_branch_as_the_employees_home_node() =>
		await RunWithDatabaseAsync("ada.sethome", async (userManager, client, rootId) => {
			var branchId = await AddBranchAsync(client, rootId);
			var console = new FakeConsoleIO([], []);

			var exitCode = await SetHomeNodeCommand.RunAsync(
				console, userManager, client, "ada.sethome", branchId, CancellationToken.None);

			exitCode.Should().Be(0);
			console.Errors.Should().BeEmpty();
			(await ReadHomeNodeAsync(client)).Should().Be(branchId);
		});

	[Fact]
	public async Task Clears_the_home_node_back_to_the_root() =>
		await RunWithDatabaseAsync("ada.clearhome", async (userManager, client, rootId) => {
			var branchId = await AddBranchAsync(client, rootId);
			var console = new FakeConsoleIO([], []);

			_ = await SetHomeNodeCommand.RunAsync(console, userManager, client, "ada.clearhome", branchId, CancellationToken.None);
			var exitCode = await SetHomeNodeCommand.RunAsync(console, userManager, client, "ada.clearhome", null, CancellationToken.None);

			exitCode.Should().Be(0);
			console.Errors.Should().BeEmpty();
			(await ReadHomeNodeAsync(client)).Should().BeNull();
		});

	[Fact]
	public async Task Rejects_a_leaf_without_changing_the_home_node() =>
		await RunWithDatabaseAsync("ada.leafhome", async (userManager, client, rootId) => {
			var branchId = await AddBranchAsync(client, rootId);
			var leafId = (await client.Query.GetJobChildrenAsync(new() { Context = Context(), ParentId = branchId })).Single().Id;
			var console = new FakeConsoleIO([], []);

			var exitCode = await SetHomeNodeCommand.RunAsync(
				console, userManager, client, "ada.leafhome", leafId, CancellationToken.None);

			exitCode.Should().Be(1);
			console.Errors.Should().ContainSingle();
			(await ReadHomeNodeAsync(client)).Should().BeNull();
		});

	[Fact]
	public async Task Fails_for_an_unknown_username() =>
		await RunWithDatabaseAsync("ada.knownhome", async (userManager, client, rootId) => {
			var branchId = await AddBranchAsync(client, rootId);
			var console = new FakeConsoleIO([], []);

			var exitCode = await SetHomeNodeCommand.RunAsync(
				console, userManager, client, "no.such.user", branchId, CancellationToken.None);

			exitCode.Should().Be(1);
			console.Errors.Should().ContainSingle(error => error.Contains("no.such.user", StringComparison.Ordinal));
			console.Lines.Should().BeEmpty();
		});

	[Fact]
	public async Task Fails_for_a_node_that_does_not_exist() =>
		await RunWithDatabaseAsync("ada.nonode", async (userManager, client, _) => {
			var console = new FakeConsoleIO([], []);

			var exitCode = await SetHomeNodeCommand.RunAsync(
				console, userManager, client, "ada.nonode", new JobNodeId(9999), CancellationToken.None);

			exitCode.Should().Be(1);
			console.Errors.Should().ContainSingle();
			(await ReadHomeNodeAsync(client)).Should().BeNull();
		});

	private static CommandContext Context() => new() { Actor = new(1), CorrelationId = Guid.NewGuid() };

	/// <summary>
	///     Runs <paramref name="body" /> against a freshly deployed SQLite database bootstrapped with
	///     <paramref name="userName" /> as its administrator (and so <c>app_user</c> id 1).
	/// </summary>
	private static async Task RunWithDatabaseAsync(
		string userName, Func<UserManager<JobTrackIdentityUser>, IJobTrackClient, JobNodeId, Task> body)
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

			var bootstrap = await client.Installation.BootstrapAdministratorAsync(
				new() {
					DisplayName = "Ada Home",
					IanaTimeZone = "Europe/London",
					UserName = userName,
					Password = KnownPassword,
					CorrelationId = Guid.NewGuid(),
				},
				CancellationToken.None);

			await body(userManager, client, bootstrap.RootJobNodeId);
		}
		finally {
			await database.DisposeAsync();
		}
	}

	/// <summary>Creates a branch (a node with one child of its own) under the tree root and returns its id.</summary>
	private static async Task<JobNodeId> AddBranchAsync(IJobTrackClient client, JobNodeId rootId)
	{
		var branch = await client.Jobs.AddChildAsync(new() {
			Context = Context(),
			ParentId = rootId,
			Description = "Build a house",
			OwnerUserId = new AppUserId(1),
			Priority = Priority.Medium,
		});

		_ = await client.Jobs.AddChildAsync(new() {
			Context = Context(),
			ParentId = branch.Id,
			Description = "Groundworks",
			OwnerUserId = new AppUserId(1),
			Priority = Priority.Medium,
		});

		return branch.Id;
	}

	private static async Task<JobNodeId?> ReadHomeNodeAsync(IJobTrackClient client) =>
		(await client.Query.GetEmployeeProfileAsync(new() { Context = Context(), TargetUserId = new(1) })).HomeNodeId;

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
