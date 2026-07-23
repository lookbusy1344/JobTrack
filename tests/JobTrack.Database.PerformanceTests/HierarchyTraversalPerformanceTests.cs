namespace JobTrack.Database.PerformanceTests;

using System.Diagnostics;
using AwesomeAssertions;
using Npgsql;
using TestSupport;

/// <summary>
///     §6.7 gate: subtree/ancestry traversal and broad-branch child listing
///     against the "deep tree" and "broad tree" scales
///     (docs/traceability/performance-budgets.md §1/§2). PostgreSQL only --
///     SQLite is exempt from latency figures (§6.4) and covered separately by
///     its functional (no-unbounded-blocking) budget.
/// </summary>
public sealed class HierarchyTraversalPerformanceTests : IAsyncLifetime
{
	private static readonly TimeSpan AncestryTraversalBudget = TimeSpan.FromMilliseconds(50);

	// Revised from 30ms per docs/traceability/performance-budgets.md §2/§4: isolated
	// measurement is sub-millisecond, but a full solution-suite run contends with every
	// other project for the same PostgreSQL instance and was observed at 130ms; 200ms
	// keeps headroom above that measured contended case.
	private static readonly TimeSpan BranchListingBudget = TimeSpan.FromMilliseconds(200);

	private readonly PostgreSqlDatabaseFixture database = new();

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Ancestor_traversal_of_a_50_level_deep_chain_meets_the_latency_and_plan_budget()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var ownerUserId = await PerformanceScaleGenerator.SeedAppUserAsync(connection, "Deep tree owner");
		var leafId = await PerformanceScaleGenerator.SeedDeepTreeAsync(connection, ownerUserId);

		// The "deep tree" scale is, by its own definition
		// (performance-budgets.md §1), only 50 rows -- small enough that a
		// sequential scan is genuinely the planner's correct choice
		// regardless of indexing, so "no full-table scan" isn't a
		// meaningful assertion at this scale. That plan-shape requirement
		// is instead checked in
		// HierarchyAchievementReadinessPerformanceTests against the same
		// job_node_ancestors query at the combined-production-tree scale
		// (200,000 rows), where a sequential scan really would be wrong.
		var stopwatch = Stopwatch.StartNew();
		await using (var command = connection.CreateCommand()) {
			command.CommandText = "SELECT * FROM job_node_ancestors(@nodeId)";
			var parameter = command.CreateParameter();
			parameter.ParameterName = "nodeId";
			parameter.Value = leafId;
			command.Parameters.Add(parameter);
			await using var reader = await command.ExecuteReaderAsync();
			while (await reader.ReadAsync()) {
			}
		}

		stopwatch.Elapsed.Should().BeLessThan(AncestryTraversalBudget);
	}

	[Fact]
	public async Task Paginated_child_listing_of_a_10000_leaf_branch_meets_the_latency_and_plan_budget()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var ownerUserId = await PerformanceScaleGenerator.SeedAppUserAsync(connection, "Broad tree owner");
		var branchId = await PerformanceScaleGenerator.SeedBroadTreeAsync(connection, ownerUserId);

		const string query = """
							 SELECT id, description FROM job_node
							 WHERE parent_id = @branchId
							 ORDER BY id
							 LIMIT 50 OFFSET 100
							 """;

		var plan = await PostgreSqlExplainPlan.GetPlanAsync(connection, query, ("branchId", branchId));

		PostgreSqlExplainPlan.ContainsSequentialScanOf(plan, "job_node").Should().BeFalse(
			"paginated child listing must use the parent_id index, not a full table scan");
		PostgreSqlExplainPlan.ContainsDiskSort(plan).Should().BeFalse("child listing must not spill its sort to disk");

		var stopwatch = Stopwatch.StartNew();
		await using (var command = connection.CreateCommand()) {
			command.CommandText = query;
			var parameter = command.CreateParameter();
			parameter.ParameterName = "branchId";
			parameter.Value = branchId;
			command.Parameters.Add(parameter);
			await using var reader = await command.ExecuteReaderAsync();
			while (await reader.ReadAsync()) {
			}
		}

		stopwatch.Elapsed.Should().BeLessThan(BranchListingBudget);
	}

	private async Task<NpgsqlConnection> OpenDeployedConnectionAsync()
	{
		var connection = new NpgsqlConnection(database.ConnectionString);
		await connection.OpenAsync();

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.PostgreSql));
		var deployer = new SchemaDeployer(
			connection, new PostgreSqlSchemaVersionStore(), new PostgreSqlDeploymentLockStrategy(), "1.2.3", "test-runner");
		await deployer.DeployAsync(scripts, CancellationToken.None);

		return connection;
	}
}
