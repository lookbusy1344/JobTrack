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
using NodaTime;
using Npgsql;
using Persistence.PostgreSql;
using Persistence.Sqlite;
using TestSupport;

/// <summary>
///     Real, schema-deployed database tests for <see cref="JobTreeImportCommand" /> — the
///     <c>import-tree</c> CLI command that turns a flat, file-local-id-keyed JSON node list into a
///     job-node subtree, all owned by one existing employee (README: "generate small trees of nodes"
///     bulk-authoring tool).
/// </summary>
public sealed class JobTreeImportCommandTests
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "admincli-tests";
	private const string KnownPassword = "correct-horse-battery-staple";

	private const string TreeJson = """
									[
										{ "id": 1, "parentId": null, "title": "Design" },
										{ "id": 2, "parentId": null, "title": "Build" },
										{ "id": 3, "parentId": 2, "title": "Build - step 1" },
										{ "id": 4, "parentId": 2, "title": "Build - step 2", "prerequisiteIds": [3] }
									]
									""";

	[Fact]
	public async Task Imports_a_multi_level_tree_with_prerequisites_under_the_root_on_sqlite()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(SchemaProvider.Sqlite, database.ConnectionString);

			var services = new ServiceCollection();
			_ = services.AddLogging();
			_ = services.AddJobTrackIdentitySqlite(database.ConnectionString);
			await using var provider = services.BuildServiceProvider();
			using var scope = provider.CreateScope();
			var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
			var client = JobTrackSqlite.Create(database.ConnectionString);

			var rootId = await BootstrapAdministratorAsync(client, "ada.import");
			var console = new FakeConsoleIO([], []);

			var exitCode = await JobTreeImportCommand.RunAsync(
				console, userManager, client, "ada.import", rootId, TreeJson, CancellationToken.None);

			exitCode.Should().Be(0);
			console.Errors.Should().BeEmpty();
			await AssertImportedTreeAsync(client, rootId);
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Imports_a_multi_level_tree_with_prerequisites_under_the_root_on_postgresql()
	{
		var database = new PostgreSqlDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(SchemaProvider.PostgreSql, database.ConnectionString);

			var services = new ServiceCollection();
			_ = services.AddLogging();
			_ = services.AddJobTrackIdentityPostgreSql(database.ConnectionString);
			await using var provider = services.BuildServiceProvider();
			using var scope = provider.CreateScope();
			var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
			await using var dataSource = new NpgsqlDataSourceBuilder(database.ConnectionString).UseNodaTime().Build();
			var client = JobTrackPostgreSql.Create(dataSource);

			var rootId = await BootstrapAdministratorAsync(client, "ada.import");
			var console = new FakeConsoleIO([], []);

			var exitCode = await JobTreeImportCommand.RunAsync(
				console, userManager, client, "ada.import", rootId, TreeJson, CancellationToken.None);

			exitCode.Should().Be(0);
			console.Errors.Should().BeEmpty();
			await AssertImportedTreeAsync(client, rootId);
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Imports_under_an_explicit_parent_node_instead_of_the_root()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(SchemaProvider.Sqlite, database.ConnectionString);

			var services = new ServiceCollection();
			_ = services.AddLogging();
			_ = services.AddJobTrackIdentitySqlite(database.ConnectionString);
			await using var provider = services.BuildServiceProvider();
			using var scope = provider.CreateScope();
			var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
			var client = JobTrackSqlite.Create(database.ConnectionString);

			var rootId = await BootstrapAdministratorAsync(client, "ada.branch");
			var context = new CommandContext { Actor = new(1), CorrelationId = Guid.NewGuid() };
			var branch = await client.Jobs.AddChildAsync(new() {
				Context = context,
				ParentId = rootId,
				Description = "Existing branch",
				OwnerUserId = new AppUserId(1),
				Priority = Priority.Medium,
			});

			const string SingleNodeJson = """[ { "id": 1, "parentId": null, "title": "Under the branch" } ]""";
			var console = new FakeConsoleIO([], []);

			var exitCode = await JobTreeImportCommand.RunAsync(
				console, userManager, client, "ada.branch", branch.Id, SingleNodeJson, CancellationToken.None);

			exitCode.Should().Be(0);
			var branchChildren = await client.Query.GetJobChildrenAsync(new() { Context = context, ParentId = branch.Id });
			branchChildren.Should().ContainSingle(n => n.Description == "Under the branch");
			var rootChildren = await client.Query.GetJobChildrenAsync(new() { Context = context, ParentId = rootId });
			rootChildren.Should().ContainSingle(n => n.Description == "Existing branch");
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Fails_without_creating_anything_for_an_unknown_username()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(SchemaProvider.Sqlite, database.ConnectionString);

			var services = new ServiceCollection();
			_ = services.AddLogging();
			_ = services.AddJobTrackIdentitySqlite(database.ConnectionString);
			await using var provider = services.BuildServiceProvider();
			using var scope = provider.CreateScope();
			var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
			var client = JobTrackSqlite.Create(database.ConnectionString);

			var rootId = await BootstrapAdministratorAsync(client, "ada.other");
			var console = new FakeConsoleIO([], []);

			var exitCode = await JobTreeImportCommand.RunAsync(
				console, userManager, client, "no.such.user", rootId, TreeJson, CancellationToken.None);

			exitCode.Should().Be(1);
			console.Errors.Should().ContainSingle(error => error.Contains("no.such.user", StringComparison.Ordinal));
			console.Lines.Should().BeEmpty();
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Fails_without_creating_anything_for_malformed_json()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(SchemaProvider.Sqlite, database.ConnectionString);

			var services = new ServiceCollection();
			_ = services.AddLogging();
			_ = services.AddJobTrackIdentitySqlite(database.ConnectionString);
			await using var provider = services.BuildServiceProvider();
			using var scope = provider.CreateScope();
			var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
			var client = JobTrackSqlite.Create(database.ConnectionString);

			var rootId = await BootstrapAdministratorAsync(client, "ada.badjson");
			var console = new FakeConsoleIO([], []);

			var exitCode = await JobTreeImportCommand.RunAsync(
				console, userManager, client, "ada.badjson", rootId, "not valid json", CancellationToken.None);

			exitCode.Should().Be(1);
			console.Errors.Should().ContainSingle();
			console.Lines.Should().BeEmpty();

			var context = new CommandContext { Actor = new(1), CorrelationId = Guid.NewGuid() };
			var rootChildren = await client.Query.GetJobChildrenAsync(new() { Context = context, ParentId = rootId });
			rootChildren.Should().BeEmpty();
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Fails_without_creating_anything_when_a_prerequisite_edge_is_invalid()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(SchemaProvider.Sqlite, database.ConnectionString);

			var services = new ServiceCollection();
			_ = services.AddLogging();
			_ = services.AddJobTrackIdentitySqlite(database.ConnectionString);
			await using var provider = services.BuildServiceProvider();
			using var scope = provider.CreateScope();
			var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
			var client = JobTrackSqlite.Create(database.ConnectionString);

			var rootId = await BootstrapAdministratorAsync(client, "ada.rollback");
			var console = new FakeConsoleIO([], []);

			// Node 2 requires its own parent (node 1) -- an ancestor/descendant edge, rejected by
			// spec §6 rule 4. Node 1 is created first in the same batch before the edge is
			// rejected, so this proves the whole import rolls back, not just the bad edge.
			const string InvalidJson = """
									   [
									   	{ "id": 1, "parentId": null, "title": "Parent" },
									   	{ "id": 2, "parentId": 1, "title": "Child", "prerequisiteIds": [1] }
									   ]
									   """;

			var exitCode = await JobTreeImportCommand.RunAsync(
				console, userManager, client, "ada.rollback", rootId, InvalidJson, CancellationToken.None);

			exitCode.Should().Be(1);
			console.Errors.Should().ContainSingle();
			console.Lines.Should().BeEmpty();

			var context = new CommandContext { Actor = new(1), CorrelationId = Guid.NewGuid() };
			var rootChildren = await client.Query.GetJobChildrenAsync(new() { Context = context, ParentId = rootId });
			rootChildren.Should().BeEmpty();
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Imports_open_and_closed_work_recorded_relative_to_the_import_instant()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(SchemaProvider.Sqlite, database.ConnectionString);

			var services = new ServiceCollection();
			_ = services.AddLogging();
			_ = services.AddJobTrackIdentitySqlite(database.ConnectionString);
			await using var provider = services.BuildServiceProvider();
			using var scope = provider.CreateScope();
			var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
			var client = JobTrackSqlite.Create(database.ConnectionString);

			var rootId = await BootstrapAdministratorAsync(client, "ada.work");
			var console = new FakeConsoleIO([], []);

			// "Survey" ran three days ago and succeeded; "Dig" depends on it, started a day ago, and is
			// still open. "Abandoned" closed unsuccessfully.
			const string WorkJson = """
									[
										{ "id": 1, "title": "Survey", "open": "3 days", "closed": "2 days" },
										{ "id": 2, "title": "Dig", "prerequisiteIds": [1], "open": "1 day" },
										{ "id": 3, "title": "Abandoned", "open": "5 days", "closed": "4 days", "outcome": "unsuccessful" }
									]
									""";

			var before = SystemClock.Instance.GetCurrentInstant();
			var exitCode = await JobTreeImportCommand.RunAsync(
				console, userManager, client, "ada.work", rootId, WorkJson, CancellationToken.None);
			var after = SystemClock.Instance.GetCurrentInstant();

			exitCode.Should().Be(0);
			console.Errors.Should().BeEmpty();

			var context = new CommandContext { Actor = new(1), CorrelationId = Guid.NewGuid() };
			var children = await client.Query.GetJobChildrenAsync(new() { Context = context, ParentId = rootId });
			var survey = children.Single(n => n.Description == "Survey");
			var dig = children.Single(n => n.Description == "Dig");
			var abandoned = children.Single(n => n.Description == "Abandoned");

			(await client.Query.GetLeafWorkAsync(new() { Context = context, JobNodeId = survey.Id }))
				.Achievement.Should().Be(Achievement.Success);
			(await client.Query.GetLeafWorkAsync(new() { Context = context, JobNodeId = dig.Id }))
				.Achievement.Should().Be(Achievement.InProgress);
			(await client.Query.GetLeafWorkAsync(new() { Context = context, JobNodeId = abandoned.Id }))
				.Achievement.Should().Be(Achievement.Unsuccessful);

			var surveySession = (await client.Query.GetLeafSessionsAsync(new() { Context = context, LeafWorkId = survey.Id })).Single();
			surveySession.StartedAt.Should().BeInRange(before - Duration.FromDays(3), after - Duration.FromDays(3));
			surveySession.FinishedAt.Should().NotBeNull();
			surveySession.FinishedAt!.Value.Should().BeInRange(before - Duration.FromDays(2), after - Duration.FromDays(2));

			var digSession = (await client.Query.GetLeafSessionsAsync(new() { Context = context, LeafWorkId = dig.Id })).Single();
			digSession.StartedAt.Should().BeInRange(before - Duration.FromDays(1), after - Duration.FromDays(1));
			digSession.FinishedAt.Should().BeNull();
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Fails_without_creating_anything_when_a_job_starts_before_its_prerequisite_finished()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(SchemaProvider.Sqlite, database.ConnectionString);

			var services = new ServiceCollection();
			_ = services.AddLogging();
			_ = services.AddJobTrackIdentitySqlite(database.ConnectionString);
			await using var provider = services.BuildServiceProvider();
			using var scope = provider.CreateScope();
			var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
			var client = JobTrackSqlite.Create(database.ConnectionString);

			var rootId = await BootstrapAdministratorAsync(client, "ada.chrono");
			var console = new FakeConsoleIO([], []);

			// Node 2 requires node 1, but starts three days ago -- a day before node 1 finished.
			const string ImpossibleJson = """
									[
										{ "id": 1, "title": "First", "open": "5 days", "closed": "2 days" },
										{ "id": 2, "title": "Second", "prerequisiteIds": [1], "open": "3 days" }
									]
									""";

			var exitCode = await JobTreeImportCommand.RunAsync(
				console, userManager, client, "ada.chrono", rootId, ImpossibleJson, CancellationToken.None);

			exitCode.Should().Be(1);
			console.Errors.Should().ContainSingle();
			console.Lines.Should().BeEmpty();

			var context = new CommandContext { Actor = new(1), CorrelationId = Guid.NewGuid() };
			var rootChildren = await client.Query.GetJobChildrenAsync(new() { Context = context, ParentId = rootId });
			rootChildren.Should().BeEmpty();
		}
		finally {
			await database.DisposeAsync();
		}
	}

	[Fact]
	public async Task Fails_without_creating_anything_for_a_malformed_work_field()
	{
		var database = new SqliteDatabaseFixture();
		await database.InitializeAsync();

		try {
			await DeploySchemaAsync(SchemaProvider.Sqlite, database.ConnectionString);

			var services = new ServiceCollection();
			_ = services.AddLogging();
			_ = services.AddJobTrackIdentitySqlite(database.ConnectionString);
			await using var provider = services.BuildServiceProvider();
			using var scope = provider.CreateScope();
			var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
			var client = JobTrackSqlite.Create(database.ConnectionString);

			var rootId = await BootstrapAdministratorAsync(client, "ada.badwork");
			var console = new FakeConsoleIO([], []);

			const string BadWorkJson = """[ { "id": 1, "title": "Vague", "open": "a little while" } ]""";

			var exitCode = await JobTreeImportCommand.RunAsync(
				console, userManager, client, "ada.badwork", rootId, BadWorkJson, CancellationToken.None);

			exitCode.Should().Be(1);
			console.Errors.Should().ContainSingle(error => error.Contains("open", StringComparison.Ordinal));
			console.Lines.Should().BeEmpty();

			var context = new CommandContext { Actor = new(1), CorrelationId = Guid.NewGuid() };
			var rootChildren = await client.Query.GetJobChildrenAsync(new() { Context = context, ParentId = rootId });
			rootChildren.Should().BeEmpty();
		}
		finally {
			await database.DisposeAsync();
		}
	}

	private static async Task<JobNodeId> BootstrapAdministratorAsync(IJobTrackClient client, string userName)
	{
		var result = await client.Installation.BootstrapAdministratorAsync(
			new() {
				DisplayName = "Ada Import",
				IanaTimeZone = "Europe/London",
				DefaultHourlyRate = new HourlyRate(20m),
				UserName = userName,
				Password = KnownPassword,
				CorrelationId = Guid.NewGuid(),
			},
			CancellationToken.None);
		return result.RootJobNodeId;
	}

	private static async Task AssertImportedTreeAsync(IJobTrackClient client, JobNodeId rootId)
	{
		var context = new CommandContext { Actor = new(1), CorrelationId = Guid.NewGuid() };

		var rootChildren = await client.Query.GetJobChildrenAsync(new() { Context = context, ParentId = rootId });
		rootChildren.Select(n => n.Description).Should().BeEquivalentTo("Design", "Build");

		var buildNode = rootChildren.Single(n => n.Description == "Build");
		var buildChildren = await client.Query.GetJobChildrenAsync(new() { Context = context, ParentId = buildNode.Id });
		buildChildren.Select(n => n.Description).Should().BeEquivalentTo("Build - step 1", "Build - step 2");

		var step1 = buildChildren.Single(n => n.Description == "Build - step 1");
		var step2 = buildChildren.Single(n => n.Description == "Build - step 2");

		var step2Prerequisites = await client.Query.GetPrerequisitesAsync(new() { Context = context, NodeId = step2.Id });
		step2Prerequisites.Should().ContainSingle(edge => edge.Equals(new(step1.Id, step2.Id)));
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
}
