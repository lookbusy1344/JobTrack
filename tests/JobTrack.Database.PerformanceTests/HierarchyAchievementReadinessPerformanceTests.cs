namespace JobTrack.Database.PerformanceTests;

using System.Diagnostics;
using AwesomeAssertions;
using Npgsql;
using TestSupport;

/// <summary>
///     §6.7 gate: recursively derived achievement, unsatisfied-prerequisite
///     explanation, and ancestor traversal at the "combined production tree"
///     scale (docs/traceability/performance-budgets.md §1/§2, ~193,500
///     job_node rows). PostgreSQL only.
/// </summary>
public sealed class HierarchyAchievementReadinessPerformanceTests : IAsyncLifetime
{
	private static readonly TimeSpan AchievementBudget = TimeSpan.FromMilliseconds(100);
	private static readonly TimeSpan UnsatisfiedPrerequisiteBudget = TimeSpan.FromMilliseconds(100);
	private static readonly TimeSpan AncestryTraversalBudget = TimeSpan.FromMilliseconds(50);

	private readonly PostgreSqlDatabaseFixture database = new();

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Recursively_derived_achievement_for_one_branch_meets_the_latency_and_plan_budget()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var ownerUserId = await PerformanceScaleGenerator.SeedAppUserAsync(connection, "Combined tree owner");
		var (_, branchId, _, _) = await PerformanceScaleGenerator.SeedCombinedProductionTreeAsync(connection, ownerUserId);

		var stopwatch = Stopwatch.StartNew();
		await using (var command = connection.CreateCommand()) {
			command.CommandText = "SELECT node_succeeded(@branchId)";
			var parameter = command.CreateParameter();
			parameter.ParameterName = "branchId";
			parameter.Value = branchId;
			command.Parameters.Add(parameter);
			_ = await command.ExecuteScalarAsync();
		}

		stopwatch.Elapsed.Should().BeLessThan(AchievementBudget);
	}

	[Fact]
	public async Task Unsatisfied_prerequisite_explanation_for_one_leaf_meets_the_latency_and_plan_budget()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var ownerUserId = await PerformanceScaleGenerator.SeedAppUserAsync(connection, "Combined tree owner");
		var (_, branchId, dependentLeafId, _) = await PerformanceScaleGenerator.SeedCombinedProductionTreeAsync(connection, ownerUserId);
		var requiredLeafId = await InsertUnsatisfiedLeafAsync(connection, ownerUserId, branchId);
		await AddPrerequisiteAsync(connection, requiredLeafId, dependentLeafId);

		var stopwatch = Stopwatch.StartNew();
		await using (var command = connection.CreateCommand()) {
			command.CommandText = "SELECT * FROM job_node_unsatisfied_prerequisites(@leafId)";
			var parameter = command.CreateParameter();
			parameter.ParameterName = "leafId";
			parameter.Value = dependentLeafId;
			command.Parameters.Add(parameter);
			await using var reader = await command.ExecuteReaderAsync();
			while (await reader.ReadAsync()) {
			}
		}

		stopwatch.Elapsed.Should().BeLessThan(UnsatisfiedPrerequisiteBudget);
	}

	[Fact]
	public async Task Ancestor_traversal_of_a_depth_15_node_in_a_200000_row_tree_meets_the_plan_budget()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var ownerUserId = await PerformanceScaleGenerator.SeedAppUserAsync(connection, "Combined tree owner");
		var (_, _, _, deepNodeId) = await PerformanceScaleGenerator.SeedCombinedProductionTreeAsync(connection, ownerUserId);

		var plan = await PostgreSqlExplainPlan.GetPlanAsync(
			connection, "SELECT * FROM job_node_ancestors(@nodeId)", ("nodeId", deepNodeId));

		PostgreSqlExplainPlan.ContainsSequentialScanOf(plan, "job_node").Should().BeFalse(
			"ancestor traversal of a 200,000-row tree must use the parent_id index, not a full table scan");

		var stopwatch = Stopwatch.StartNew();
		await using (var command = connection.CreateCommand()) {
			command.CommandText = "SELECT * FROM job_node_ancestors(@nodeId)";
			var parameter = command.CreateParameter();
			parameter.ParameterName = "nodeId";
			parameter.Value = deepNodeId;
			command.Parameters.Add(parameter);
			await using var reader = await command.ExecuteReaderAsync();
			while (await reader.ReadAsync()) {
			}
		}

		stopwatch.Elapsed.Should().BeLessThan(AncestryTraversalBudget);
	}

	private static async Task<long> InsertUnsatisfiedLeafAsync(NpgsqlConnection connection, long ownerUserId, long parentId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  WITH inserted AS (
							      INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
							      VALUES (@parentId, 'Unsatisfied leaf', @ownerUserId, @ownerUserId, 2, now())
							      RETURNING id
							  )
							  INSERT INTO leaf_work (job_node_id, changed_at)
							  SELECT id, now() FROM inserted
							  RETURNING job_node_id;
							  """;
		var parentParameter = command.CreateParameter();
		parentParameter.ParameterName = "parentId";
		parentParameter.Value = parentId;
		command.Parameters.Add(parentParameter);
		var ownerParameter = command.CreateParameter();
		ownerParameter.ParameterName = "ownerUserId";
		ownerParameter.Value = ownerUserId;
		command.Parameters.Add(ownerParameter);

		return (long)(await command.ExecuteScalarAsync())!;
	}

	private static async Task AddPrerequisiteAsync(NpgsqlConnection connection, long fromId, long toId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT add_job_prerequisite(@fromId, @toId)";
		var fromParameter = command.CreateParameter();
		fromParameter.ParameterName = "fromId";
		fromParameter.Value = fromId;
		command.Parameters.Add(fromParameter);
		var toParameter = command.CreateParameter();
		toParameter.ParameterName = "toId";
		toParameter.Value = toId;
		command.Parameters.Add(toParameter);
		_ = await command.ExecuteNonQueryAsync();
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
