namespace JobTrack.Database.PerformanceTests;

using AwesomeAssertions;
using Npgsql;
using TestSupport;

/// <summary>
///     §8.1/§8.2 of docs/plans/2026-07-09-overlapping-cost-scale-plan.md: structural invariants of
///     <see cref="PerformanceScaleGenerator.SeedOverlappingCostScaleAsync" />, checked on a small
///     parameterised instance (3 workers x 8 leaves, depth 3) so a shape defect is caught before scaling
///     to the full 50x400 dataset used by the performance test.
/// </summary>
public sealed class OverlappingCostScaleGeneratorTests : IAsyncLifetime
{
	private const int WorkerCount = 3;
	private const int LeavesPerWorker = 8;
	private const int TotalLeafCount = WorkerCount * LeavesPerWorker;
	private const int OverlapDepth = 3;
	private static readonly TimeSpan Slot = TimeSpan.FromHours(1);
	private static readonly DateTimeOffset BaseInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private readonly PostgreSqlDatabaseFixture database = new();

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Seeds_the_expected_row_counts()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		_ = await PerformanceScaleGenerator.SeedOverlappingCostScaleAsync(
			connection, BaseInstant, WorkerCount, TotalLeafCount, OverlapDepth, false);

		(await CountAsync(connection, """
									  job_node n
									  WHERE n.parent_id IS NOT NULL
									    AND EXISTS (SELECT 1 FROM job_node c WHERE c.parent_id = n.id)
									  """)).Should().Be(WorkerCount);
		(await CountAsync(connection, """
									  job_node n
									  WHERE NOT EXISTS (SELECT 1 FROM job_node c WHERE c.parent_id = n.id)
									    AND EXISTS (SELECT 1 FROM leaf_work lw WHERE lw.job_node_id = n.id)
									  """)).Should().Be(TotalLeafCount);
		(await CountAsync(connection, "leaf_work")).Should().Be(TotalLeafCount);
		(await CountAsync(connection, "work_session")).Should().Be(TotalLeafCount);
		(await CountAsync(connection, "user_schedule_version")).Should().Be(WorkerCount);
		(await CountAsync(connection, "user_schedule_interval")).Should().Be(WorkerCount * 7);
		(await CountAsync(connection, "user_cost_rate")).Should().Be(WorkerCount * 3);
		(await CountAsync(connection, "job_prerequisite")).Should().Be(WorkerCount * (LeavesPerWorker - 1));
	}

	[Fact]
	public async Task Per_worker_concurrency_follows_the_closed_form_staircase_with_no_same_leaf_overlap()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var seed = await PerformanceScaleGenerator.SeedOverlappingCostScaleAsync(
			connection, BaseInstant, WorkerCount, TotalLeafCount, OverlapDepth, false);

		var workerIds = await QueryWorkerIdsAsync(connection, seed.OneBranchId);
		foreach (var workerId in workerIds) {
			var sessions = await QuerySessionsAsync(connection, workerId);
			sessions.Should().HaveCount(LeavesPerWorker);

			var totalSlots = LeavesPerWorker + OverlapDepth - 1;
			var observedMax = 0;
			for (var m = 1; m <= totalSlots; m++) {
				var sampleInstant = BaseInstant + TimeSpan.FromTicks((Slot.Ticks * (m - 1)) + (Slot.Ticks / 2));
				var observed = sessions.Count(session => session.StartedAt <= sampleInstant && sampleInstant < session.FinishedAt);
				var expected = ExpectedDepth(m, LeavesPerWorker, OverlapDepth);
				observed.Should().Be(expected, $"worker {workerId} slot {m} should match the closed-form staircase depth");
				observedMax = Math.Max(observedMax, observed);
			}

			observedMax.Should().Be(OverlapDepth, "the interior of the staircase must reach exactly the configured overlap depth");
		}
	}

	[Fact]
	public async Task Prerequisite_edges_are_acyclic_and_never_a_hierarchy_edge()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var seed = await PerformanceScaleGenerator.SeedOverlappingCostScaleAsync(
			connection, BaseInstant, WorkerCount, TotalLeafCount, OverlapDepth, false);

		// The schema's deferred constraint triggers (check_job_prerequisite_no_cycle,
		// check_job_prerequisite_not_hierarchy_edge) already ran at commit for every inserted edge --
		// SeedOverlappingCostScaleAsync completing without throwing is itself the proof. This test
		// additionally pins the from_id < to_id shape the generator claims to construct.
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM job_prerequisite WHERE from_id >= to_id;";
		var backwardOrSelfEdges = (long)(await command.ExecuteScalarAsync())!;
		backwardOrSelfEdges.Should().Be(0);
		_ = seed;
	}

	private static int ExpectedDepth(int slot, int leafCount, int depth)
	{
		var lower = Math.Max(1, slot - depth + 1);
		var upper = Math.Min(leafCount, slot);
		return Math.Max(0, upper - lower + 1);
	}

	private static async Task<long> CountAsync(NpgsqlConnection connection, string fromAndWhere)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT COUNT(*) FROM {fromAndWhere};";
		return (long)(await command.ExecuteScalarAsync())!;
	}

	private static async Task<long[]> QueryWorkerIdsAsync(NpgsqlConnection connection, long oneBranchId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT owner_user_id FROM job_node n
							  WHERE n.parent_id = (SELECT parent_id FROM job_node WHERE id = @oneBranchId)
							    AND EXISTS (SELECT 1 FROM job_node c WHERE c.parent_id = n.id);
							  """;
		command.Parameters.AddWithValue("oneBranchId", oneBranchId);

		var ids = new List<long>();
		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			ids.Add(reader.GetInt64(0));
		}

		return [.. ids];
	}

	private static async Task<List<(DateTimeOffset StartedAt, DateTimeOffset FinishedAt)>> QuerySessionsAsync(NpgsqlConnection connection,
		long workerId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT started_at, finished_at FROM work_session
							  WHERE worked_by_user_id = @workerId
							  ORDER BY started_at;
							  """;
		command.Parameters.AddWithValue("workerId", workerId);

		var sessions = new List<(DateTimeOffset, DateTimeOffset)>();
		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			sessions.Add((reader.GetFieldValue<DateTimeOffset>(0), reader.GetFieldValue<DateTimeOffset>(1)));
		}

		return sessions;
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
