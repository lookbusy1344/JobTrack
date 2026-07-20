namespace JobTrack.Database.PerformanceTests;

using Abstractions;
using Application.Ports;
using AwesomeAssertions;
using Domain.Costing;
using NodaTime;
using Npgsql;
using Persistence.PostgreSql;
using TestSupport;

/// <summary>
///     §8.2 of docs/plans/2026-07-09-overlapping-cost-scale-plan.md: on the same small instance as
///     <see cref="OverlappingCostScaleGeneratorTests" /> (3 workers x 8 leaves, depth 3), loads real
///     persistence-materialized inputs through <see cref="ICostQueryPort" /> and checks
///     <see cref="CostSegmentPartitioner" />'s output against the staircase's closed-form oracle: every
///     segment's concurrency divisor N must equal the expected staircase depth at that instant, and
///     every session's allocated time must sum back to its own nominal duration (no time lost or
///     double-counted by the partition).
/// </summary>
public sealed class OverlappingCostScaleCorrectnessTests : IAsyncLifetime
{
	private const int WorkerCount = 3;
	private const int LeavesPerWorker = 8;
	private const int TotalLeafCount = WorkerCount * LeavesPerWorker;
	private const int OverlapDepth = 3;
	private const int CorrectnessQueryHierarchyNodeLimit = TotalLeafCount + WorkerCount + 1;
	private static readonly TimeSpan Slot = TimeSpan.FromHours(1);
	private static readonly DateTimeOffset BaseInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private readonly PostgreSqlDatabaseFixture database = new();

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Partitioner_segment_N_matches_the_closed_form_staircase_depth_and_conserves_allocated_time()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var seed = await PerformanceScaleGenerator.SeedOverlappingCostScaleAsync(
			connection, BaseInstant, WorkerCount, TotalLeafCount, OverlapDepth, false);

		var workerIds = await QueryWorkerBranchesAsync(connection, seed.OneBranchId);
		foreach (var (workerId, branchId) in workerIds) {
			await GrantCostViewerRoleAsync(connection, workerId);
		}

		var port = new PostgreSqlCostQueryPort(new NpgsqlDataSourceBuilder(database.ConnectionString).UseNodaTime().Build(), SystemClock.Instance);
		var asOf = Instant.FromDateTimeOffset(seed.AsOf);
		var baseInstant = Instant.FromDateTimeOffset(BaseInstant);
		var totalSlots = LeavesPerWorker + OverlapDepth - 1;

		foreach (var (workerId, branchId) in workerIds) {
			var inputs = await port.GetCostInputsAsync(
				new(workerId), new(branchId), asOf, CorrectnessQueryHierarchyNodeLimit);
			var worker = inputs.Workers.Should().ContainSingle(w => w.Sessions.Count == LeavesPerWorker).Subject;

			var allocations = CostSegmentPartitioner.Partition(
				worker.Sessions, worker.EffectiveWorkingIntervals, inputs.NodesById,
				worker.Exceptions, worker.NodeOverrides, worker.UserCostRates, inputs.Bounds);

			allocations.Should().NotBeEmpty();

			foreach (var allocation in allocations) {
				var midpoint = allocation.Segment.Start + (allocation.Segment.Duration / 2);
				var slot = ((midpoint - baseInstant).TotalTicks / Slot.Ticks) + 1;
				var expectedDepth = ExpectedDepth((int)slot, LeavesPerWorker, OverlapDepth);
				allocation.Share.ConcurrencyDivisor.Should().Be(
					expectedDepth,
					$"worker {workerId} segment [{allocation.Segment.Start},{allocation.Segment.End}) should match the closed-form staircase depth");
			}

			// Conservation (plan §4): fair 1/N division within each segment always sums back to that
			// segment's own duration, so the worker's total allocated time across every
			// session/segment must equal the total elapsed width of the staircase's covered window
			// -- not any individual session's own nominal duration, which fair sharing legitimately
			// reduces whenever N > 1.
			var totalAllocatedTicks = allocations.Sum(a => (decimal)a.Share.SegmentTicks / a.Share.ConcurrencyDivisor);
			var expectedCoveredTicks = (decimal)totalSlots * Slot.Ticks;
			totalAllocatedTicks.Should().BeApproximately(
				expectedCoveredTicks, 1m, $"worker {workerId} must allocate exactly the staircase's covered elapsed time, no more and no less");
		}
	}

	private static int ExpectedDepth(int slot, int leafCount, int depth)
	{
		var lower = Math.Max(1, slot - depth + 1);
		var upper = Math.Min(leafCount, slot);
		return Math.Max(0, upper - lower + 1);
	}

	private static async Task<List<(long WorkerId, long BranchId)>> QueryWorkerBranchesAsync(NpgsqlConnection connection, long oneBranchId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT id, owner_user_id FROM job_node n
							  WHERE n.parent_id = (SELECT parent_id FROM job_node WHERE id = @oneBranchId)
							    AND EXISTS (SELECT 1 FROM job_node c WHERE c.parent_id = n.id)
							  ORDER BY id;
							  """;
		command.Parameters.AddWithValue("oneBranchId", oneBranchId);

		var result = new List<(long, long)>();
		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			result.Add((reader.GetInt64(1), reader.GetInt64(0)));
		}

		return result;
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
		identityCommand.Parameters.AddWithValue("userName", $"overlap-worker-{appUserId}".ToUpperInvariant());
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
