namespace JobTrack.Database.PerformanceTests;

using System.Diagnostics;
using AwesomeAssertions;
using Npgsql;
using TestSupport;

/// <summary>
///     §6.7 gate: empty-database schema deployment latency
///     (docs/traceability/performance-budgets.md §2). The "upgrade from the
///     oldest supported version at combined-production-tree scale" budget row
///     is deliberately not covered here -- see that file's note on why it is
///     deferred. PostgreSQL only; SQLite deployment has no equivalent latency
///     budget (§6.4).
/// </summary>
public sealed class SchemaDeploymentPerformanceTests : IAsyncLifetime
{
	private static readonly TimeSpan EmptyDeploymentBudget = TimeSpan.FromSeconds(30);

	private readonly PostgreSqlDatabaseFixture database = new();

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Deploying_every_schema_version_to_an_empty_database_meets_the_latency_budget()
	{
		await using var connection = new NpgsqlConnection(database.ConnectionString);
		await connection.OpenAsync();

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.PostgreSql));
		var deployer = new SchemaDeployer(
			connection, new PostgreSqlSchemaVersionStore(), new PostgreSqlDeploymentLockStrategy(), "1.2.3", "test-runner");

		var stopwatch = Stopwatch.StartNew();
		await deployer.DeployAsync(scripts, CancellationToken.None);
		await PostgreSqlRolesAndGrants.ApplyAsync(connection, RepositoryPaths.PostgreSqlRolesAndGrantsScriptPath(), CancellationToken.None);
		stopwatch.Stop();

		stopwatch.Elapsed.Should().BeLessThan(EmptyDeploymentBudget);
	}
}
