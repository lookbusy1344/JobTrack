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
///     The "long history" scale's two deferred budget rows (performance-budgets.md §2: cost
///     calculation for one leaf / one 100-leaf branch, both against 5 years of daily
///     <c>work_session</c> history for 20 users) plus the DB-materialisation-vs-pure-engine
///     profiling that 2026-08-06-cost-read-materialisation-reduction-plan.md Stage 1 used to
///     retarget Stage 4 from a schedule-expansion-volume hypothesis to
///     <c>IntervalAlgebra.Subtract</c>'s measured O(minuend x cuts) defect. PostgreSQL only, per
///     that plan's scope.
/// </summary>
/// <remarks>
///     <see cref="LeafCostBudget" />/<see cref="BranchCostBudget" /> are revised measured ceilings,
///     not the original 150 ms/2 s pre-implementation targets, per
///     `docs/traceability/performance-budgets.md` §4's revision policy ("a budget proven wrong by
///     measurement is revised here, with the reason recorded, not silently loosened at the test").
///     History: Stage 4 fixed the materialisation-stage defect it targeted (DB-and-CPU input
///     assembly ~7,690 ms → ~184 ms), which unmasked <see cref="Domain.Costing.CostSegmentPartitioner" />/
///     <see cref="Domain.Costing.CostEngine" />'s own per-allocation rate resolution scanning the
///     worker's full schedule-exception list (O(allocations × exceptions)); the 2026-08-06 follow-up
///     fixed that too (<c>RateResolver.FilterPricedExceptions</c>) plus the hot loops' allocation
///     churn, measured leaf ~586-658 ms / branch ~344-356 ms over repeated serialized runs. The leaf
///     figure is dominated by first-call process warm-up (it runs first: EF model build, pool
///     spin-up), not by the read itself -- the branch read on the same warmed process is the truer
///     query figure. Ceilings carry ~1.35-1.4x headroom over the highest observed run.
/// </remarks>
public sealed class LongHistoryScalePerformanceTests : IAsyncLifetime
{
	private const int ScaleQueryHierarchyNodeLimit = 50_000;
	private static readonly TimeSpan LeafCostBudget = TimeSpan.FromMilliseconds(800);
	private static readonly TimeSpan BranchCostBudget = TimeSpan.FromMilliseconds(500);
	private static readonly DateTimeOffset BaseInstant = new(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private readonly PostgreSqlDatabaseFixture database = new();
	private readonly ITestOutputHelper output;

	public LongHistoryScalePerformanceTests(ITestOutputHelper output) => this.output = output;

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Cost_calculation_for_one_leaf_and_the_20_worker_branch_meets_the_latency_and_plan_budget()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var seed = await PerformanceScaleGenerator.SeedLongHistoryScaleAsync(connection, BaseInstant);
		output.WriteLine($"Seed: {seed.Seed}");
		await GrantCostViewerRoleAsync(connection, seed.OwnerActorId);

		var dataSource = new NpgsqlDataSourceBuilder(database.ConnectionString).UseNodaTime().Build();
		var port = new PostgreSqlCostQueryPort(dataSource, SystemClock.Instance);
		var costQueries = new CostQueries(port);
		var context = new CommandContext {
			Actor = new(seed.OwnerActorId),
			CorrelationId = Guid.NewGuid(),
		};
		var asOf = Instant.FromDateTimeOffset(seed.AsOf);

		var leafStopwatch = Stopwatch.StartNew();
		var leafResult = await costQueries.GetCostDetailsAsync(
			new() {
				Context = context,
				NodeId = new(seed.OneLeafId),
				AsOf = asOf,
			});
		leafStopwatch.Stop();
		output.WriteLine(
			$"Leaf cost details, 5-year history ({LeafCostBudget.TotalMilliseconds:F0} ms budget): " +
			$"{leafStopwatch.Elapsed.TotalMilliseconds:F1} ms, exact={leafResult.ExactCost}");

		var branchStopwatch = Stopwatch.StartNew();
		var branchResult = await costQueries.GetHierarchyTotalsAsync(
			new() {
				Context = context,
				NodeId = new(seed.BranchId),
				AsOf = asOf,
			});
		branchStopwatch.Stop();
		output.WriteLine(
			$"Branch (20-worker, 5-year history) hierarchy totals ({BranchCostBudget.TotalMilliseconds:F0} ms budget): " +
			$"{branchStopwatch.Elapsed.TotalMilliseconds:F1} ms, nodes={branchResult.ExactCosts.Count}");

		// DB-materialization vs pure-engine breakdown, mirroring OverlappingCostScalePerformanceTests
		// -- plus, per 2026-08-06-cost-read-materialisation-reduction-plan.md §2.1/Stage 1, the
		// expanded-working-interval count each worker's schedule produces against this scale's ~5-year
		// window (the evidence that retargeted Stage 4 to IntervalAlgebra.Subtract instead).
		var portStopwatch = Stopwatch.StartNew();
		var inputs = await port.GetCostInputsAsync(new(seed.BranchId), asOf, ScaleQueryHierarchyNodeLimit);
		portStopwatch.Stop();

		var totalExpandedIntervals = inputs.Workers.Sum(worker => worker.ScheduledWorkingIntervals.Count);
		var totalSessions = inputs.Workers.Sum(worker => worker.Sessions.Count);
		output.WriteLine(
			$"DB materialization: {portStopwatch.Elapsed.TotalMilliseconds:F1} ms; workers={inputs.Workers.Count}; " +
			$"total scheduled working intervals={totalExpandedIntervals}; total sessions={totalSessions}; " +
			$"intervals-per-session={(totalSessions == 0 ? 0.0 : (double)totalExpandedIntervals / totalSessions):F2}");

		var engineStopwatch = Stopwatch.StartNew();
		foreach (var worker in inputs.Workers) {
			var allocations = CostSegmentPartitioner.Partition(
				worker.Sessions, worker.EffectiveWorkingIntervals, inputs.NodesById,
				worker.Exceptions, worker.NodeOverrides, worker.UserCostRates, inputs.Bounds);
			_ = CostEngine.Calculate(
				new(seed.BranchId), allocations, inputs.NodesById, worker.ScheduledWorkingIntervals, worker.Exceptions, worker.NodeOverrides,
				worker.UserCostRates, worker.UserDefaultRate);
		}

		engineStopwatch.Stop();
		output.WriteLine($"Pure engine (partition + calculate, all workers): {engineStopwatch.Elapsed.TotalMilliseconds:F1} ms");

		leafResult.ExactCost.Amount.Should().BePositive();
		leafStopwatch.Elapsed.Should().BeLessThan(LeafCostBudget);
		branchStopwatch.Elapsed.Should().BeLessThan(BranchCostBudget);
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
		identityCommand.Parameters.AddWithValue("userName", $"long-history-worker-{appUserId}".ToUpperInvariant());
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
		var connection = await PerformanceScaleGenerator.OpenConnectionForSeedingAsync(database.ConnectionString);

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.PostgreSql));
		var deployer = new SchemaDeployer(
			connection, new PostgreSqlSchemaVersionStore(), new PostgreSqlDeploymentLockStrategy(), "1.2.3", "test-runner");
		await deployer.DeployAsync(scripts, CancellationToken.None);

		return connection;
	}
}
