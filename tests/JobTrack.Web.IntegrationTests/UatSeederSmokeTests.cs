namespace JobTrack.Web.IntegrationTests;

using Application;
using AwesomeAssertions;
using Database;
using Microsoft.Data.Sqlite;
using NodaTime;
using Persistence.Sqlite;
using TestSupport;
using UatSeed;

/// <summary>
///     Proves the end-user-testing readiness synthetic seed (remediation plan §2.3) applies cleanly to a
///     freshly deployed, freshly bootstrapped database, matching how a UAT operator would run it after
///     README.md's "Running on a development server" steps. Uses SQLite since it needs no separate
///     server; the PostgreSQL path shares the same <see cref="UatSeeder.SeedAsync" /> and
///     <see cref="IJobTrackClient" /> contract, so PostgreSQL-specific coverage would be duplication, not
///     additional evidence.
/// </summary>
public sealed class UatSeederSmokeTests : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private const string ConfigureSqliteConnectionSql =
		"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";

	private readonly SqliteDatabaseFixture database = new();

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task The_seed_applies_to_a_freshly_bootstrapped_database_and_produces_the_expected_scenario()
	{
		await DeploySchemaAsync();
		var client = JobTrackSqlite.Create(database.ConnectionString);
		var bootstrap = await client.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.uat-seed-smoke",
			Password = "Bootstrap-Horse-Battery-77!",
			CorrelationId = Guid.NewGuid(),
		});

		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using (var pragma = connection.CreateCommand()) {
			pragma.CommandText = ConfigureSqliteConnectionSql;
			_ = await pragma.ExecuteNonQueryAsync();
		}

		var summary = await UatSeeder.SeedAsync(client, connection, bootstrap.AdministratorId);

		var context = new CommandContext { Actor = bootstrap.AdministratorId, CorrelationId = Guid.NewGuid() };
		var unassignedRequest = await client.Requests.GetDetailAsync(new() { Context = context, NodeId = summary.UnassignedRequestNodeId });
		unassignedRequest.AcknowledgedAt.Should().BeNull();

		var assignedRequest = await client.Query.GetJobNodeAsync(new() { Context = context, NodeId = summary.AssignedRequestNodeId });
		assignedRequest.Node.OwnerUserId.Should().Be(summary.WorkerId);

		var poolLeaf = await client.Query.GetJobNodeAsync(new() { Context = context, NodeId = summary.PoolLeafNodeId });
		poolLeaf.Node.OwnerUserId.Should().BeNull();

		var readiness = await client.Query.GetReadinessAsync(new() { Context = context, NodeId = summary.BlockedLeafNodeId });
		readiness.IsReady.Should().BeFalse();

		var workerContext = new CommandContext { Actor = summary.WorkerId, CorrelationId = Guid.NewGuid() };
		var activeSessions = await client.Query.GetActiveSessionsAsync(new() {
			Context = workerContext,
			LeafWorkIds = [summary.ActiveSessionLeafNodeId],
		});
		activeSessions.Should().ContainSingle();

		var costDetail = await client.Costs.GetCostDetailsAsync(new() {
			Context = context,
			NodeId = summary.CostReportableLeafNodeId,
			AsOf = SystemClock.Instance.GetCurrentInstant(),
		});
		costDetail.Trace.Should().NotBeEmpty();
		costDetail.DisplayedCost.Amount.Should().BePositive();
	}

	private async Task DeploySchemaAsync()
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using (var pragma = connection.CreateCommand()) {
			pragma.CommandText = ConfigureSqliteConnectionSql;
			_ = await pragma.ExecuteNonQueryAsync();
		}

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.Sqlite));
		var deployer = new SchemaDeployer(connection, new SqliteSchemaVersionStore(), new SqliteDeploymentLockStrategy(), ApplicationVersion,
			AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}
}
