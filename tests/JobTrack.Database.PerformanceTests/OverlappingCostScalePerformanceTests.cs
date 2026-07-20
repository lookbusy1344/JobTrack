namespace JobTrack.Database.PerformanceTests;

using System.Diagnostics;
using Abstractions;
using Application;
using AwesomeAssertions;
using Domain.Costing;
using NodaTime;
using Npgsql;
using Persistence.PostgreSql;
using TestSupport;
using Xunit.Abstractions;

/// <summary>
///     §6/§7 of docs/plans/2026-07-09-overlapping-cost-scale-plan.md: cost-calculation latency and
///     query-plan budgets, measured end to end through <see cref="CostQueries" /> (including EF
///     materialization, per performance-budgets.md §2's own wording) against the 50x400
///     "overlapping-cost scale" -- 50 workers, 20,000 leaves, a 6-deep per-worker session staircase,
///     24x7 schedules, and a per-worker rate timeline. PostgreSQL only; no SQLite latency budget
///     (§6.4's single-writer exemption -- see <c>OverlappingCostScaleSqliteFunctionalTests</c>).
/// </summary>
public sealed class OverlappingCostScalePerformanceTests : IAsyncLifetime
{
	private const int ScaleQueryHierarchyNodeLimit = 50_000;
	private static readonly TimeSpan LeafCostBudget = TimeSpan.FromMilliseconds(150);
	private static readonly TimeSpan BranchCostBudget = TimeSpan.FromSeconds(2);
	private static readonly DateTimeOffset BaseInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private readonly PostgreSqlDatabaseFixture database = new();
	private readonly ITestOutputHelper output;

	public OverlappingCostScalePerformanceTests(ITestOutputHelper output) => this.output = output;

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Cost_calculation_for_one_leaf_and_one_branch_meets_the_latency_and_plan_budget()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var seed = await PerformanceScaleGenerator.SeedOverlappingCostScaleAsync(connection, BaseInstant, includeHeavyWorker: true);
		await GrantCostViewerRoleAsync(connection, seed.OwnerActorId);

		// CostQueryAssembly.LoadWorkersAsync now fetches through worker_overlapping_sessions
		// (schema version 0018) rather than duplicating its predicate in LINQ -- this is the exact
		// query shape it issues, per worker, for EXPLAIN purposes. This particular query spans the
		// worker's *entire* history (bounds = the full staircase window), so a plain btree scan
		// keyed on worked_by_user_id genuinely is cheaper than GiST here -- 100% selectivity gives
		// GiST no pruning to do, so the planner correctly skips it (see
		// Cost_calculation_for_a_late_leaf_in_a_long_history_uses_the_gist_index_not_a_full_history_scan
		// below for the narrow-window case that actually exercises the GiST index).
		const string sessionLoadQuery = "SELECT * FROM worker_overlapping_sessions(@workerId, @boundsStart, @boundsEnd, @asOf)";
		(string Name, object Value)[] planParameters = [
			("workerId", seed.OwnerActorId),
			("boundsStart", BaseInstant),
			("boundsEnd", seed.AsOf),
			("asOf", seed.AsOf),
		];
		var plan = await PostgreSqlExplainPlan.GetPlanAsync(connection, sessionLoadQuery, planParameters);
		PostgreSqlExplainPlan.ContainsSequentialScanOf(plan, "work_session").Should().BeFalse(
			"the cost-input session load must use an index, not a sequential scan of work_session");

		var dataSource = new NpgsqlDataSourceBuilder(database.ConnectionString).UseNodaTime().Build();
		var port = new PostgreSqlCostQueryPort(dataSource, SystemClock.Instance);
		var costQueries = new CostQueries(port);
		var context = new CommandContext { Actor = new(seed.OwnerActorId), CorrelationId = Guid.NewGuid() };
		var asOf = Instant.FromDateTimeOffset(seed.AsOf);

		var leafStopwatch = Stopwatch.StartNew();
		var leafResult = await costQueries.GetCostDetailsAsync(
			new() { Context = context, NodeId = new(seed.OneLeafId), AsOf = asOf });
		leafStopwatch.Stop();
		output.WriteLine($"Leaf cost details (150 ms budget): {leafStopwatch.Elapsed.TotalMilliseconds:F1} ms, exact={leafResult.ExactCost}");

		var branchStopwatch = Stopwatch.StartNew();
		var branchResult = await costQueries.GetHierarchyTotalsAsync(
			new() { Context = context, NodeId = new(seed.OneBranchId), AsOf = asOf });
		branchStopwatch.Stop();
		output.WriteLine(
			$"Branch (400-leaf, single-worker) hierarchy totals (2 s budget): {branchStopwatch.Elapsed.TotalMilliseconds:F1} ms, " +
			$"nodes={branchResult.ExactCosts.Count}");

		// DB-materialization vs pure-engine breakdown (plan §6 step 4), over the same branch query.
		var portStopwatch = Stopwatch.StartNew();
		var inputs = await port.GetCostInputsAsync(
			new(seed.OwnerActorId), new(seed.OneBranchId), asOf, ScaleQueryHierarchyNodeLimit);
		portStopwatch.Stop();

		var engineStopwatch = Stopwatch.StartNew();
		foreach (var worker in inputs.Workers) {
			var allocations = CostSegmentPartitioner.Partition(
				worker.Sessions, worker.EffectiveWorkingIntervals, inputs.NodesById,
				worker.Exceptions, worker.NodeOverrides, worker.UserCostRates, inputs.Bounds);
			_ = CostEngine.Calculate(
				new(seed.OneBranchId), allocations, inputs.NodesById, worker.ScheduledWorkingIntervals, worker.Exceptions, worker.NodeOverrides,
				worker.UserCostRates, worker.UserDefaultRate);
		}

		engineStopwatch.Stop();
		output.WriteLine(
			$"DB materialization: {portStopwatch.Elapsed.TotalMilliseconds:F1} ms; pure engine: {engineStopwatch.Elapsed.TotalMilliseconds:F1} ms");

		leafResult.ExactCost.Amount.Should().BePositive();
		leafStopwatch.Elapsed.Should().BeLessThan(LeafCostBudget);
		branchStopwatch.Elapsed.Should().BeLessThan(BranchCostBudget);
	}

	/// <summary>
	///     Plan §4's "optional worst-case addendum": one worker with 5,000 sessions in the same
	///     staircase shape, to bound the partitioner's O(P^2) tail that 400-session workers don't
	///     reveal. Reported, not budget-asserted -- performance-budgets.md carries no separate row for
	///     this deliberately unrealistic worst case (plan §7).
	/// </summary>
	[Fact]
	public async Task Heavy_worker_with_5000_sessions_bounds_the_partitioners_quadratic_tail()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var seed = await PerformanceScaleGenerator.SeedOverlappingCostScaleAsync(connection, BaseInstant, includeHeavyWorker: true);
		seed.HeavyWorkerId.Should().NotBeNull();
		seed.HeavyWorkerBranchId.Should().NotBeNull();
		await GrantCostViewerRoleAsync(connection, seed.HeavyWorkerId!.Value);

		var port = new PostgreSqlCostQueryPort(new NpgsqlDataSourceBuilder(database.ConnectionString).UseNodaTime().Build(), SystemClock.Instance);
		var costQueries = new CostQueries(port);
		var context = new CommandContext { Actor = new(seed.HeavyWorkerId!.Value), CorrelationId = Guid.NewGuid() };
		var asOf = Instant.FromDateTimeOffset(seed.AsOf);

		var stopwatch = Stopwatch.StartNew();
		var result = await costQueries.GetHierarchyTotalsAsync(
			new() { Context = context, NodeId = new(seed.HeavyWorkerBranchId!.Value), AsOf = asOf });
		stopwatch.Stop();

		output.WriteLine(
			$"Heavy worker (5,000 sessions) hierarchy totals: {stopwatch.Elapsed.TotalMilliseconds:F1} ms, nodes={result.ExactCosts.Count}");
		result.ExactCosts.Should().NotBeEmpty();
	}

	/// <summary>
	///     Regression test for the GiST-index fix (schema version 0018):
	///     <see
	///         cref="Application.CostQueries.GetCostDetailsAsync" />
	///     's <c>bounds.Start</c> is the requested
	///     subtree's own earliest session start, so requesting one <em>late</em> leaf under the heavy
	///     worker (5,000 sessions, ~208-day span) produces a narrow window against most of that
	///     worker's history -- unlike the whole-branch queries above, which always span the full
	///     history and could never distinguish the fixed query plan from the one it replaced.
	/// </summary>
	[Fact]
	public async Task Cost_calculation_for_a_late_leaf_in_a_long_history_uses_the_gist_index_not_a_full_history_scan()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var seed = await PerformanceScaleGenerator.SeedOverlappingCostScaleAsync(connection, BaseInstant, includeHeavyWorker: true);
		seed.HeavyWorkerBranchId.Should().NotBeNull();
		await GrantCostViewerRoleAsync(connection, seed.HeavyWorkerId!.Value);

		var lateLeafId = await QueryLastLeafIdAsync(connection, seed.HeavyWorkerBranchId!.Value);

		const string sessionLoadQuery = "SELECT * FROM worker_overlapping_sessions(@workerId, @boundsStart, @boundsEnd, @asOf)";
		(string Name, object Value)[] planParameters = [
			("workerId", seed.HeavyWorkerId!.Value),
			// The late leaf's own session starts near the end of the worker's ~208-day history, so
			// this narrow bound (unlike the whole-branch queries above) actually exercises the
			// worker's non-matching prior history -- exactly the scenario that exposed the bug.
			("boundsStart", seed.AsOf.AddDays(-1)),
			("boundsEnd", seed.AsOf),
			("asOf", seed.AsOf),
		];
		var plan = await PostgreSqlExplainPlan.GetPlanAsync(connection, sessionLoadQuery, planParameters);
		PostgreSqlExplainPlan.ContainsIndexScanUsing(plan, "work_session_user_range_gist_idx").Should().BeTrue(
			"a narrow query against a long-lived worker's history must use the GiST range index, not scan the whole history");

		var port = new PostgreSqlCostQueryPort(new NpgsqlDataSourceBuilder(database.ConnectionString).UseNodaTime().Build(), SystemClock.Instance);
		var costQueries = new CostQueries(port);
		var context = new CommandContext { Actor = new(seed.HeavyWorkerId!.Value), CorrelationId = Guid.NewGuid() };
		var asOf = Instant.FromDateTimeOffset(seed.AsOf);

		var stopwatch = Stopwatch.StartNew();
		var result = await costQueries.GetCostDetailsAsync(
			new() { Context = context, NodeId = new(lateLeafId), AsOf = asOf });
		stopwatch.Stop();

		output.WriteLine($"Late leaf in long history cost details: {stopwatch.Elapsed.TotalMilliseconds:F1} ms, exact={result.ExactCost}");
		result.ExactCost.Amount.Should().BePositive();
		stopwatch.Elapsed.Should().BeLessThan(LeafCostBudget);
	}

	private static async Task<long> QueryLastLeafIdAsync(NpgsqlConnection connection, long branchId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT jn.id
							  FROM job_node jn
							  JOIN leaf_work lw ON lw.job_node_id = jn.id
							  WHERE jn.parent_id = @branchId
							  ORDER BY jn.id DESC
							  LIMIT 1;
							  """;
		command.Parameters.AddWithValue("branchId", branchId);
		return (long)(await command.ExecuteScalarAsync())!;
	}

	private static async Task GrantCostViewerRoleAsync(NpgsqlConnection connection, long appUserId)
	{
		await using var identityCommand = connection.CreateCommand();
		identityCommand.CommandText = """
									  INSERT INTO identity_user
									  	(app_user_id, user_name, normalized_user_name, password_hash, security_stamp,
									  	 concurrency_stamp, requires_password_change, is_enabled, lockout_enabled, access_failed_count)
									  VALUES
									  	(@appUserId, @userName, @userName, 'test-hash', @securityStamp, @concurrencyStamp, false, true, true, 0)
									  RETURNING id;
									  """;
		identityCommand.Parameters.AddWithValue("appUserId", appUserId);
		identityCommand.Parameters.AddWithValue("userName", $"overlap-scale-worker-{appUserId}".ToUpperInvariant());
		identityCommand.Parameters.AddWithValue("securityStamp", Guid.NewGuid().ToString("N"));
		identityCommand.Parameters.AddWithValue("concurrencyStamp", Guid.NewGuid().ToString("N"));
		var identityUserId = (long)(await identityCommand.ExecuteScalarAsync())!;

		await using var roleCommand = connection.CreateCommand();
		roleCommand.CommandText = """
								  INSERT INTO identity_user_role (identity_user_id, identity_role_id)
								  VALUES (@identityUserId, @roleId);
								  """;
		roleCommand.Parameters.AddWithValue("identityUserId", identityUserId);
		roleCommand.Parameters.AddWithValue("roleId", (short)EmployeeRole.CostViewer);
		_ = await roleCommand.ExecuteNonQueryAsync();
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
