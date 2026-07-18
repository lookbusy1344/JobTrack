namespace JobTrack.Database.PerformanceTests;

using System.Diagnostics;
using AwesomeAssertions;
using Npgsql;
using TestSupport;

/// <summary>
///     §6.7 gate: effective-dated rate lookup against the "many users" scale
///     (docs/traceability/performance-budgets.md §1/§2: 2,000 users, each with
///     a 10-change rate timeline over 5 years). PostgreSQL only.
/// </summary>
public sealed class RateResolutionPerformanceTests : IAsyncLifetime
{
	private static readonly TimeSpan RateResolutionBudget = TimeSpan.FromMilliseconds(20);

	private readonly PostgreSqlDatabaseFixture database = new();

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Resolve_rate_for_one_user_among_2000_meets_the_latency_and_plan_budget()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var timelineStart = new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);
		var userId = await PerformanceScaleGenerator.SeedManyUsersAsync(connection, timelineStart);
		var ownerUserId = await PerformanceScaleGenerator.SeedAppUserAsync(connection, "Rate lookup owner");
		var nodeId = await InsertRootNodeAsync(connection, ownerUserId);
		var lookupAt = timelineStart.AddYears(3);

		const string query = "SELECT resolve_rate(@nodeId, @userId, @at)";
		(string Name, object Value)[] parameters = [("nodeId", nodeId), ("userId", userId), ("at", lookupAt)];

		var plan = await PostgreSqlExplainPlan.GetPlanAsync(connection, query, parameters);

		// resolve_rate is called here as a scalar target-list expression, which PostgreSQL never
		// inlines -- EXPLAIN only ever shows a bare "Result" node for it, with none of the
		// function's internal scans visible. user_rate_boundaries below reads the exact same
		// generated range columns (effective_range/exception_range, remediation plan §3.1) but is
		// called as a set-returning function in a FROM clause, which the planner does inline, so
		// its EXPLAIN output is the meaningful proof that all three rate sources use the GiST
		// index backing their own table's exclusion constraint rather than a scan.
		PostgreSqlExplainPlan.ContainsSequentialScanOf(plan, "user_cost_rate").Should().BeFalse(
			"rate resolution must use the user_id index, not a scan of the whole rate table");

		var boundariesPlan = await PostgreSqlExplainPlan.GetPlanAsync(
			connection,
			"SELECT * FROM user_rate_boundaries(@userId, @nodeId, @from, @to)",
			("userId", userId), ("nodeId", nodeId), ("from", lookupAt.AddDays(-1)), ("to", lookupAt.AddDays(1)));

		PostgreSqlExplainPlan.ContainsIndexScanUsing(boundariesPlan, "user_cost_rate_no_overlap_per_user").Should().BeTrue(
			"rate-boundary discovery must use the GiST index backing user_cost_rate's exclusion constraint via effective_range");
		PostgreSqlExplainPlan.ContainsIndexScanUsing(boundariesPlan, "node_rate_override_no_overlap_per_node_and_user").Should().BeTrue(
			"rate-boundary discovery must use the GiST index backing node_rate_override's exclusion constraint via effective_range");
		PostgreSqlExplainPlan.ContainsIndexScanUsing(boundariesPlan, "user_schedule_exception_no_overlap_priced_additive").Should().BeTrue(
			"priced-exception boundary discovery must use the GiST index backing user_schedule_exception's exclusion constraint via exception_range");

		var stopwatch = Stopwatch.StartNew();
		await using (var command = connection.CreateCommand()) {
			command.CommandText = query;
			foreach (var (name, value) in parameters) {
				var parameter = command.CreateParameter();
				parameter.ParameterName = name;
				parameter.Value = value;
				command.Parameters.Add(parameter);
			}

			var resolvedRate = (decimal)(await command.ExecuteScalarAsync())!;
			resolvedRate.Should().BePositive();
		}

		stopwatch.Elapsed.Should().BeLessThan(RateResolutionBudget);
	}

	private static async Task<long> InsertRootNodeAsync(NpgsqlConnection connection, long ownerUserId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
							  VALUES (NULL, 'Rate lookup root', @ownerUserId, @ownerUserId, @priorityId, now())
							  RETURNING id;
							  """;
		command.Parameters.AddWithValue("ownerUserId", ownerUserId);
		command.Parameters.AddWithValue("priorityId", (short)2);
		return (long)(await command.ExecuteScalarAsync())!;
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
