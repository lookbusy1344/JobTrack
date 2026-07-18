namespace JobTrack.TestSupport;

using Npgsql;

/// <summary>
///     Builds the representative dataset scales from
///     docs/traceability/performance-budgets.md §1, entirely server-side (one
///     or a handful of set-based <c>INSERT ... SELECT</c> statements per
///     scale) so fixture setup for a 200,000-row tree stays fast -- these
///     scales exist to measure the *query* budgets in §2/§3, not to exercise
///     the insert path itself.
/// </summary>
public static class PerformanceScaleGenerator
{
	private const short PriorityMedium = 2;

	// Overlapping-cost scale (docs/plans/2026-07-09-overlapping-cost-scale-plan.md §4/§5).
	private const int OverlapDefaultWorkerCount = 50;
	private const int OverlapDefaultTotalLeafCount = 20_000;
	private const int OverlapDefaultDepth = 6;
	private const int OverlapDefaultHeavyWorkerSessionCount = 5_000;
	private const decimal OverlapDefaultHourlyRate = 20.00m;
	private const decimal OverlapRateEdgeStep = 5.00m;

	// A foreign-key check takes a row-level lock on the referenced row for
	// the rest of the transaction, and every distinct referenced row within
	// one statement/transaction consumes one shared-memory lock-table slot
	// (default max_locks_per_transaction=64 x max_connections=100 ~= 6,400
	// slots cluster-wide, shared with every other concurrently running
	// test). A single INSERT touching tens of thousands of distinct parent
	// or referenced rows exhausts that pool ("out of shared memory",
	// PostgreSQL error 53200) -- max_locks_per_transaction is a
	// postmaster-context setting, not changeable per session, so scale
	// generation batches every bulk insert against a bounded number of
	// distinct referenced rows instead of relying on a larger server
	// configuration.
	private const int LockSafeBatchSize = 300;
	private static readonly TimeSpan OverlapSlotDuration = TimeSpan.FromHours(1);

	public static async Task<long> SeedAppUserAsync(NpgsqlConnection connection, string displayName)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO app_user (display_name, iana_time_zone, default_hourly_rate)
							  VALUES (@displayName, 'Europe/London', 20.00)
							  RETURNING id;
							  """;
		command.Parameters.AddWithValue("displayName", displayName);
		return (long)(await command.ExecuteScalarAsync())!;
	}

	/// <summary>
	///     "Deep tree" (§1): one hierarchy chain 50 levels deep, single child
	///     per level. Returns the deepest (leaf) node's id.
	/// </summary>
	public static async Task<long> SeedDeepTreeAsync(NpgsqlConnection connection, long ownerUserId, int depth = 50)
	{
		// A recursive CTE cannot both INSERT and recurse over its own
		// inserted rows in PostgreSQL, so the chain is built with a
		// server-side PL/pgSQL loop instead -- one round trip total, not
		// one client round trip per level. PostgreSQL's DO command accepts
		// no bind parameters, so the (generator-internal, never
		// caller/user-supplied) ids and counts are interpolated directly
		// into the anonymous block rather than bound.
		await using var command = connection.CreateCommand();
		command.CommandText = $"""
							   DO $$
							   DECLARE
							       v_parent_id bigint;
							       v_new_id bigint;
							       v_level int;
							   BEGIN
							       INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
							       VALUES (NULL, 'Deep level 1', {ownerUserId}, {ownerUserId}, {PriorityMedium}, now())
							       RETURNING id INTO v_parent_id;

							       FOR v_level IN 2..{depth} LOOP
							           INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
							           VALUES (v_parent_id, 'Deep level ' || v_level, {ownerUserId}, {ownerUserId}, {PriorityMedium}, now())
							           RETURNING id INTO v_new_id;
							           v_parent_id := v_new_id;
							       END LOOP;

							       CREATE TEMP TABLE IF NOT EXISTS deep_tree_result (leaf_id bigint) ON COMMIT PRESERVE ROWS;
							       DELETE FROM deep_tree_result;
							       INSERT INTO deep_tree_result VALUES (v_parent_id);
							   END
							   $$;
							   """;
		_ = await command.ExecuteNonQueryAsync();

		await using var readBack = connection.CreateCommand();
		readBack.CommandText = "SELECT leaf_id FROM deep_tree_result;";
		return (long)(await readBack.ExecuteScalarAsync())!;
	}

	/// <summary>
	///     "Broad tree" (§1): one branch with <paramref name="leafCount" /> direct
	///     leaf-work children. Returns the branch node's id.
	/// </summary>
	public static async Task<long> SeedBroadTreeAsync(NpgsqlConnection connection, long ownerUserId, int leafCount = 10_000)
	{
		await using var rootCommand = connection.CreateCommand();
		rootCommand.CommandText = """
								  INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
								  VALUES (NULL, 'Broad root', @ownerUserId, @ownerUserId, @priorityId, now())
								  RETURNING id;
								  """;
		rootCommand.Parameters.AddWithValue("ownerUserId", ownerUserId);
		rootCommand.Parameters.AddWithValue("priorityId", PriorityMedium);
		var rootId = (long)(await rootCommand.ExecuteScalarAsync())!;

		await using var branchCommand = connection.CreateCommand();
		branchCommand.CommandText = """
									INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
									VALUES (@rootId, 'Broad branch', @ownerUserId, @ownerUserId, @priorityId, now())
									RETURNING id;
									""";
		branchCommand.Parameters.AddWithValue("rootId", rootId);
		branchCommand.Parameters.AddWithValue("ownerUserId", ownerUserId);
		branchCommand.Parameters.AddWithValue("priorityId", PriorityMedium);
		var branchId = (long)(await branchCommand.ExecuteScalarAsync())!;

		for (var offset = 0; offset < leafCount; offset += LockSafeBatchSize) {
			var batchCount = Math.Min(LockSafeBatchSize, leafCount - offset);

			await using var leavesCommand = connection.CreateCommand();
			leavesCommand.CommandText = """
										WITH inserted AS (
										    INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
										    SELECT @branchId, 'Broad leaf ' || g, @ownerUserId, @ownerUserId, @priorityId, now()
										    FROM generate_series(1, @batchCount) AS g
										    RETURNING id
										)
										INSERT INTO leaf_work (job_node_id, changed_at)
										SELECT id, now() FROM inserted;
										""";
			leavesCommand.Parameters.AddWithValue("branchId", branchId);
			leavesCommand.Parameters.AddWithValue("ownerUserId", ownerUserId);
			leavesCommand.Parameters.AddWithValue("priorityId", PriorityMedium);
			leavesCommand.Parameters.AddWithValue("batchCount", batchCount);
			_ = await leavesCommand.ExecuteNonQueryAsync();
		}

		return branchId;
	}

	/// <summary>
	///     "Combined production tree" (§1): approximately 200,000 <c>job_node</c>
	///     rows, median depth 6, max depth 15. Built level-by-level with a
	///     branching factor chosen so most nodes land at depth 6, plus a thin
	///     single-child chain extending one depth-6 node down to depth 15 for
	///     the max-depth outlier. Returns (rootId, aMidDepthBranchId, aLeafId,
	///     theDepth15NodeId).
	/// </summary>
	public static async Task<(long RootId, long BranchId, long LeafId, long DeepNodeId)> SeedCombinedProductionTreeAsync(
		NpgsqlConnection connection, long ownerUserId)
	{
		await using var rootCommand = connection.CreateCommand();
		rootCommand.CommandText = """
								  INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
								  VALUES (NULL, 'Combined root', @ownerUserId, @ownerUserId, @priorityId, now())
								  RETURNING id;
								  """;
		rootCommand.Parameters.AddWithValue("ownerUserId", ownerUserId);
		rootCommand.Parameters.AddWithValue("priorityId", PriorityMedium);
		var rootId = (long)(await rootCommand.ExecuteScalarAsync())!;

		// Levels 1-5 are branches; each level's branching factor is chosen
		// so level 6 (the bulk of the tree) lands close to 180,000 leaves.
		int[] branchingFactors = [10, 5, 6, 7, 7];
		var previousLevelIds = new[] { rootId };

		foreach (var branchingFactor in branchingFactors) {
			previousLevelIds = await InsertLevelAsync(connection, previousLevelIds, branchingFactor, ownerUserId);
		}

		var branchIdForLeaves = previousLevelIds[0];
		var leafIds = await InsertLevelAsync(connection, previousLevelIds, 12, ownerUserId);

		await InsertLeafWorkInBatchesAsync(connection, leafIds);

		// Extend one depth-6 branch node down to depth 15 (nine more
		// single-child branch levels) for the max-depth outlier; this
		// contributes a negligible row count.
		var deepChainParent = branchIdForLeaves;
		for (var level = 0; level < 9; level++) {
			await using var chainCommand = connection.CreateCommand();
			chainCommand.CommandText = """
									   INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
									   VALUES (@parentId, 'Deep chain extension', @ownerUserId, @ownerUserId, @priorityId, now())
									   RETURNING id;
									   """;
			chainCommand.Parameters.AddWithValue("parentId", deepChainParent);
			chainCommand.Parameters.AddWithValue("ownerUserId", ownerUserId);
			chainCommand.Parameters.AddWithValue("priorityId", PriorityMedium);
			deepChainParent = (long)(await chainCommand.ExecuteScalarAsync())!;
		}

		// A table just bulk-loaded has no statistics yet, which can make the query planner
		// pick a plan a production database -- whose autovacuum daemon keeps statistics current
		// as nodes accumulate gradually -- would never choose (schema version 0018's header,
		// SeedOverlappingCostScaleAsync's identical fixture-staleness fix below).
		await using (var analyzeCommand = connection.CreateCommand()) {
			analyzeCommand.CommandText = "ANALYZE job_node; ANALYZE leaf_work;";
			_ = await analyzeCommand.ExecuteNonQueryAsync();
		}

		return (rootId, branchIdForLeaves, leafIds[0], deepChainParent);
	}

	private static async Task<long[]> InsertLevelAsync(
		NpgsqlConnection connection, long[] parentIds, int childrenPerParent, long ownerUserId)
	{
		var ids = new List<long>();

		for (var offset = 0; offset < parentIds.Length; offset += LockSafeBatchSize) {
			var batch = parentIds[offset..Math.Min(offset + LockSafeBatchSize, parentIds.Length)];

			await using var command = connection.CreateCommand();
			command.CommandText = """
								  INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
								  SELECT p, 'Combined node', @ownerUserId, @ownerUserId, @priorityId, now()
								  FROM unnest(@parentIds) AS p
								  CROSS JOIN generate_series(1, @childrenPerParent)
								  RETURNING id;
								  """;
			command.Parameters.AddWithValue("parentIds", batch);
			command.Parameters.AddWithValue("ownerUserId", ownerUserId);
			command.Parameters.AddWithValue("priorityId", PriorityMedium);
			command.Parameters.AddWithValue("childrenPerParent", childrenPerParent);

			await using var reader = await command.ExecuteReaderAsync();
			while (await reader.ReadAsync()) {
				ids.Add(reader.GetInt64(0));
			}
		}

		return [.. ids];
	}

	private static async Task InsertLeafWorkInBatchesAsync(NpgsqlConnection connection, long[] leafIds)
	{
		for (var offset = 0; offset < leafIds.Length; offset += LockSafeBatchSize) {
			var batch = leafIds[offset..Math.Min(offset + LockSafeBatchSize, leafIds.Length)];

			await using var command = connection.CreateCommand();
			command.CommandText = """
								  INSERT INTO leaf_work (job_node_id, changed_at)
								  SELECT id, now() FROM unnest(@leafIds) AS id;
								  """;
			command.Parameters.AddWithValue("leafIds", batch);
			_ = await command.ExecuteNonQueryAsync();
		}
	}

	/// <summary>
	///     "High concurrency" (§1): one worker with 100 concurrent open
	///     <c>work_session</c> rows across 100 different leaves at the same
	///     instant. Returns the worker's user id.
	/// </summary>
	public static async Task<long> SeedHighConcurrencyWorkerAsync(NpgsqlConnection connection, DateTimeOffset instant, int concurrentSessions = 100)
	{
		var userId = await SeedAppUserAsync(connection, "High concurrency worker");

		await using var rootCommand = connection.CreateCommand();
		rootCommand.CommandText = """
								  INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
								  VALUES (NULL, 'Concurrency root', @userId, @userId, @priorityId, now())
								  RETURNING id;
								  """;
		rootCommand.Parameters.AddWithValue("userId", userId);
		rootCommand.Parameters.AddWithValue("priorityId", PriorityMedium);
		var rootId = (long)(await rootCommand.ExecuteScalarAsync())!;

		await using var leavesAndSessionsCommand = connection.CreateCommand();
		leavesAndSessionsCommand.CommandText = """
											   WITH inserted_leaves AS (
											       INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
											       SELECT @rootId, 'Concurrency leaf ' || g, @userId, @userId, @priorityId, now()
											       FROM generate_series(1, @count) AS g
											       RETURNING id
											   ),
											   inserted_leaf_work AS (
											       INSERT INTO leaf_work (job_node_id, changed_at)
											       SELECT id, now() FROM inserted_leaves
											       RETURNING job_node_id
											   )
											   INSERT INTO work_session (leaf_work_id, worked_by_user_id, started_at, finished_at, changed_at)
											   SELECT job_node_id, @userId, @startedAt, NULL, now() FROM inserted_leaf_work;
											   """;
		leavesAndSessionsCommand.Parameters.AddWithValue("rootId", rootId);
		leavesAndSessionsCommand.Parameters.AddWithValue("userId", userId);
		leavesAndSessionsCommand.Parameters.AddWithValue("priorityId", PriorityMedium);
		leavesAndSessionsCommand.Parameters.AddWithValue("count", concurrentSessions);
		leavesAndSessionsCommand.Parameters.AddWithValue("startedAt", instant.AddHours(-1));
		_ = await leavesAndSessionsCommand.ExecuteNonQueryAsync();

		return userId;
	}

	/// <summary>
	///     "Many users" (§1): 2,000 <c>app_user</c> rows, each with an
	///     effective-dated rate timeline of 10 changes over 5 years. Returns
	///     one representative user's id.
	/// </summary>
	public static async Task<long> SeedManyUsersAsync(NpgsqlConnection connection, DateTimeOffset timelineStart, int userCount = 2_000,
		int ratesPerUser = 10)
	{
		await using var usersCommand = connection.CreateCommand();
		usersCommand.CommandText = """
								   INSERT INTO app_user (display_name, iana_time_zone, default_hourly_rate)
								   SELECT 'Many-users worker ' || g, 'Europe/London', 15.00
								   FROM generate_series(1, @userCount) AS g
								   RETURNING id;
								   """;
		usersCommand.Parameters.AddWithValue("userCount", userCount);

		var userIds = new List<long>();
		await using (var reader = await usersCommand.ExecuteReaderAsync()) {
			while (await reader.ReadAsync()) {
				userIds.Add(reader.GetInt64(0));
			}
		}

		for (var offset = 0; offset < userIds.Count; offset += LockSafeBatchSize) {
			var batch = userIds.GetRange(offset, Math.Min(LockSafeBatchSize, userIds.Count - offset));

			await using var ratesCommand = connection.CreateCommand();
			ratesCommand.CommandText = """
									   INSERT INTO user_cost_rate (user_id, effective_start, effective_end, rate, changed_at)
									   SELECT
									       u,
									       @timelineStart + make_interval(days => (r - 1) * (365 * 5 / @ratesPerUser)),
									       CASE WHEN r = @ratesPerUser THEN NULL
									            ELSE @timelineStart + make_interval(days => r * (365 * 5 / @ratesPerUser))
									       END,
									       20.00 + r,
									       now()
									   FROM unnest(@userIds) AS u
									   CROSS JOIN generate_series(1, @ratesPerUser) AS r;
									   """;
			ratesCommand.Parameters.AddWithValue("timelineStart", timelineStart);
			ratesCommand.Parameters.AddWithValue("ratesPerUser", ratesPerUser);
			ratesCommand.Parameters.AddWithValue("userIds", batch.ToArray());
			_ = await ratesCommand.ExecuteNonQueryAsync();
		}

		return userIds[^1];
	}

	/// <summary>
	///     "Overlapping-cost scale" (plan §4/§5): <paramref name="workerCount" /> workers each owning
	///     <paramref name="totalLeafCount" />/<paramref name="workerCount" /> leaves, with a per-worker
	///     sliding-window staircase of <c>work_session</c> rows reaching exactly
	///     <paramref name="overlapDepth" />-deep concurrency in its interior, a 24x7 weekly schedule, a
	///     3-edge rate timeline crossing the staircase window, and one forward (acyclic-by-construction)
	///     prerequisite edge per adjacent leaf pair. Optionally adds one extra "heavy" worker with
	///     <paramref name="heavyWorkerSessionCount" /> sessions in the same staircase shape, to bound the
	///     partitioner's O(P^2) tail (plan §4's "optional worst-case addendum").
	/// </summary>
	public static async Task<OverlappingCostScaleSeed> SeedOverlappingCostScaleAsync(
		NpgsqlConnection connection,
		DateTimeOffset baseInstant,
		int workerCount = OverlapDefaultWorkerCount,
		int totalLeafCount = OverlapDefaultTotalLeafCount,
		int overlapDepth = OverlapDefaultDepth,
		bool includeHeavyWorker = true,
		int heavyWorkerSessionCount = OverlapDefaultHeavyWorkerSessionCount)
	{
		if (workerCount <= 0) {
			throw new ArgumentOutOfRangeException(nameof(workerCount), workerCount, "Worker count must be positive.");
		}

		if (totalLeafCount % workerCount != 0) {
			throw new ArgumentException(
				$"Total leaf count ({totalLeafCount}) must be evenly divisible by worker count ({workerCount}).", nameof(totalLeafCount));
		}

		var leavesPerWorker = totalLeafCount / workerCount;
		if (leavesPerWorker < overlapDepth) {
			throw new ArgumentException(
				$"Leaves per worker ({leavesPerWorker}) must be at least the overlap depth ({overlapDepth}).", nameof(totalLeafCount));
		}

		var appUserCount = workerCount + (includeHeavyWorker ? 1 : 0);
		var workerIds = await InsertOverlapWorkersAsync(connection, appUserCount);
		var heavyWorkerId = includeHeavyWorker ? workerIds[^1] : (long?)null;

		var rootId = await InsertOverlapRootAsync(connection, workerIds[0]);
		var branchIdByWorker = await InsertOverlapBranchesAsync(connection, rootId, workerIds);

		foreach (var workerId in workerIds) {
			var leafCount = workerId == heavyWorkerId ? heavyWorkerSessionCount : leavesPerWorker;
			var branchId = branchIdByWorker[workerId];
			await InsertOverlapLeavesAsync(connection, branchId, workerId, leafCount);
			await InsertStaircaseSessionsAsync(connection, branchId, workerId, leafCount, overlapDepth, baseInstant, OverlapSlotDuration);
			await InsertChainPrerequisitesAsync(connection, branchId, leafCount);
		}

		await InsertWeekly24x7SchedulesAsync(connection, workerIds, baseInstant);

		var normalWindow = OverlapWindowDuration(leavesPerWorker, overlapDepth, OverlapSlotDuration);
		var windowEnd = baseInstant + normalWindow;
		await InsertRateTimelineAsync(connection, workerIds, baseInstant, windowEnd);

		var longestLeafCount = includeHeavyWorker ? Math.Max(leavesPerWorker, heavyWorkerSessionCount) : leavesPerWorker;
		var asOf = baseInstant + OverlapWindowDuration(longestLeafCount, overlapDepth, OverlapSlotDuration) + TimeSpan.FromHours(1);

		// A table just bulk-loaded has no statistics yet (or stale ones from before this seed run),
		// which can make the query planner pick a plan a production database -- whose autovacuum
		// daemon keeps statistics current as sessions accumulate gradually -- would never choose
		// (schema version 0018's header). Measuring against artificially stale statistics would
		// misrepresent steady-state production latency, not just this fixture's own setup cost.
		await using (var analyzeCommand = connection.CreateCommand()) {
			analyzeCommand.CommandText = "ANALYZE work_session;";
			_ = await analyzeCommand.ExecuteNonQueryAsync();
		}

		var oneWorkerId = workerIds[0];
		var oneBranchId = branchIdByWorker[oneWorkerId];
		var oneLeafId = await QueryFirstLeafIdAsync(connection, oneBranchId);

		var heavyWorkerBranchId = heavyWorkerId is { } heavyId ? branchIdByWorker[heavyId] : (long?)null;

		var seed = $"workers={workerCount};leavesPerWorker={leavesPerWorker};depth={overlapDepth};" +
				   $"base={baseInstant:O};heavyWorker={includeHeavyWorker};heavyWorkerSessions={heavyWorkerSessionCount}";

		return new(oneWorkerId, oneLeafId, oneBranchId, asOf, seed, heavyWorkerId, heavyWorkerBranchId);
	}

	private static TimeSpan OverlapWindowDuration(int leafCount, int overlapDepth, TimeSpan slotDuration) =>
		TimeSpan.FromTicks(slotDuration.Ticks * (leafCount - 1 + overlapDepth));

	private static async Task<long[]> InsertOverlapWorkersAsync(NpgsqlConnection connection, int count)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO app_user (display_name, iana_time_zone, default_hourly_rate)
							  SELECT 'Overlap worker ' || g, 'Europe/London', @defaultHourlyRate
							  FROM generate_series(1, @count) AS g
							  RETURNING id;
							  """;
		command.Parameters.AddWithValue("count", count);
		command.Parameters.AddWithValue("defaultHourlyRate", OverlapDefaultHourlyRate);

		var ids = new List<long>();
		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			ids.Add(reader.GetInt64(0));
		}

		return [.. ids];
	}

	private static async Task<long> InsertOverlapRootAsync(NpgsqlConnection connection, long ownerUserId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
							  VALUES (NULL, 'Overlap root', @ownerUserId, @ownerUserId, @priorityId, now())
							  RETURNING id;
							  """;
		command.Parameters.AddWithValue("ownerUserId", ownerUserId);
		command.Parameters.AddWithValue("priorityId", PriorityMedium);
		return (long)(await command.ExecuteScalarAsync())!;
	}

	/// <summary>
	///     Inserts one branch per worker under <paramref name="rootId" />, keyed by <c>owner_user_id</c>
	///     (unique per worker) rather than relying on any implicit correspondence between
	///     <c>RETURNING</c> row order and the input array's order.
	/// </summary>
	private static async Task<Dictionary<long, long>> InsertOverlapBranchesAsync(NpgsqlConnection connection, long rootId, long[] workerIds)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
							  SELECT @rootId, 'Overlap worker branch', u, u, @priorityId, now()
							  FROM unnest(@workerIds) AS u
							  RETURNING id, owner_user_id;
							  """;
		command.Parameters.AddWithValue("rootId", rootId);
		command.Parameters.AddWithValue("priorityId", PriorityMedium);
		command.Parameters.AddWithValue("workerIds", workerIds);

		var branchIdByWorker = new Dictionary<long, long>();
		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			branchIdByWorker[reader.GetInt64(1)] = reader.GetInt64(0);
		}

		return branchIdByWorker;
	}

	private static async Task InsertOverlapLeavesAsync(NpgsqlConnection connection, long branchId, long workerId, int leafCount)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  WITH inserted AS (
							      INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
							      SELECT @branchId, 'Overlap leaf ' || g, @workerId, @workerId, @priorityId, now()
							      FROM generate_series(1, @leafCount) AS g
							      RETURNING id
							  )
							  INSERT INTO leaf_work (job_node_id, changed_at)
							  SELECT id, now() FROM inserted;
							  """;
		command.Parameters.AddWithValue("branchId", branchId);
		command.Parameters.AddWithValue("workerId", workerId);
		command.Parameters.AddWithValue("priorityId", PriorityMedium);
		command.Parameters.AddWithValue("leafCount", leafCount);
		_ = await command.ExecuteNonQueryAsync();
	}

	/// <summary>
	///     Builds the sliding-window staircase (plan §4): leaf <c>k</c> (1-based, ranked by
	///     <c>job_node.id</c> ascending -- monotonic with insertion order, so this is independent of any
	///     <c>RETURNING</c> row-order assumption) gets one session
	///     <c>[t0 + (k-1)*S, t0 + (k-1+D)*S)</c>. Batched by <c>k</c>-range so one statement never
	///     references more than <see cref="LockSafeBatchSize" /> distinct <c>leaf_work</c> rows.
	/// </summary>
	private static async Task InsertStaircaseSessionsAsync(
		NpgsqlConnection connection, long branchId, long workerId, int leafCount, int overlapDepth, DateTimeOffset baseInstant, TimeSpan slotDuration)
	{
		for (var lo = 1; lo <= leafCount; lo += LockSafeBatchSize) {
			var hi = Math.Min(lo + LockSafeBatchSize - 1, leafCount);

			await using var command = connection.CreateCommand();
			command.CommandText = """
								  WITH ranked AS (
								      SELECT lw.job_node_id AS leaf_id, ROW_NUMBER() OVER (ORDER BY lw.job_node_id) AS k
								      FROM leaf_work lw
								      JOIN job_node jn ON jn.id = lw.job_node_id
								      WHERE jn.parent_id = @branchId
								  )
								  INSERT INTO work_session (leaf_work_id, worked_by_user_id, started_at, finished_at, changed_at)
								  SELECT leaf_id, @workerId,
								         @baseInstant + make_interval(secs => (k - 1) * @slotSeconds),
								         @baseInstant + make_interval(secs => (k - 1 + @depth) * @slotSeconds),
								         now()
								  FROM ranked
								  WHERE k BETWEEN @lo AND @hi;
								  """;
			command.Parameters.AddWithValue("branchId", branchId);
			command.Parameters.AddWithValue("workerId", workerId);
			command.Parameters.AddWithValue("baseInstant", baseInstant);
			command.Parameters.AddWithValue("slotSeconds", slotDuration.TotalSeconds);
			command.Parameters.AddWithValue("depth", overlapDepth);
			command.Parameters.AddWithValue("lo", lo);
			command.Parameters.AddWithValue("hi", hi);
			_ = await command.ExecuteNonQueryAsync();
		}
	}

	/// <summary>
	///     One forward prerequisite edge per adjacent leaf pair (<c>leaf_k -> leaf_(k+1)</c>), acyclic by
	///     construction since <c>k</c>-ranking guarantees <c>from_id &lt; to_id</c>. Batched by
	///     <c>k</c>-range; a batch of up to <see cref="LockSafeBatchSize" /> chained edges touches at most
	///     <see cref="LockSafeBatchSize" /> + 1 distinct <c>job_node</c> rows.
	/// </summary>
	private static async Task InsertChainPrerequisitesAsync(NpgsqlConnection connection, long branchId, int leafCount)
	{
		for (var lo = 1; lo < leafCount; lo += LockSafeBatchSize) {
			var hi = Math.Min(lo + LockSafeBatchSize - 1, leafCount - 1);

			await using var command = connection.CreateCommand();
			command.CommandText = """
								  WITH ranked AS (
								      SELECT lw.job_node_id AS leaf_id, ROW_NUMBER() OVER (ORDER BY lw.job_node_id) AS k
								      FROM leaf_work lw
								      JOIN job_node jn ON jn.id = lw.job_node_id
								      WHERE jn.parent_id = @branchId
								  )
								  INSERT INTO job_prerequisite (from_id, to_id)
								  SELECT a.leaf_id, b.leaf_id
								  FROM ranked a
								  JOIN ranked b ON b.k = a.k + 1
								  WHERE a.k BETWEEN @lo AND @hi;
								  """;
			command.Parameters.AddWithValue("branchId", branchId);
			command.Parameters.AddWithValue("lo", lo);
			command.Parameters.AddWithValue("hi", hi);
			_ = await command.ExecuteNonQueryAsync();
		}
	}

	/// <summary>
	///     One 24x7 weekly schedule per worker, open-ended from <paramref name="baseInstant" />'s date.
	///     Each day's interval is <c>[00:00, 23:59:59)</c> rather than a midnight-to-midnight
	///     <c>crosses_midnight</c> interval -- the domain's <c>WeeklyInterval</c> rejects an equal
	///     start/end as ambiguous (plan §4's "deliberate simplification") and, per the temporal
	///     hardening plan's Gap D, a sub-second boundary; the resulting one-second daily gap never
	///     lands on an hour-aligned staircase boundary.
	/// </summary>
	private static async Task InsertWeekly24x7SchedulesAsync(NpgsqlConnection connection, long[] workerIds, DateTimeOffset baseInstant)
	{
		await using var versionCommand = connection.CreateCommand();
		versionCommand.CommandText = """
									 INSERT INTO user_schedule_version (user_id, effective_start, effective_end, iana_time_zone)
									 SELECT u, @effectiveStart, NULL, 'Europe/London'
									 FROM unnest(@workerIds) AS u
									 RETURNING id;
									 """;
		versionCommand.Parameters.AddWithValue("effectiveStart", new DateOnly(baseInstant.Year, baseInstant.Month, baseInstant.Day));
		versionCommand.Parameters.AddWithValue("workerIds", workerIds);

		var scheduleVersionIds = new List<long>();
		await using (var reader = await versionCommand.ExecuteReaderAsync()) {
			while (await reader.ReadAsync()) {
				scheduleVersionIds.Add(reader.GetInt64(0));
			}
		}

		await using var intervalCommand = connection.CreateCommand();
		intervalCommand.CommandText = """
									  INSERT INTO user_schedule_interval (schedule_version_id, day_of_week, start_time, end_time, crosses_midnight)
									  SELECT sv, dow, TIME '00:00:00', TIME '23:59:59', false
									  FROM unnest(@scheduleVersionIds) AS sv
									  CROSS JOIN generate_series(1, 7) AS dow;
									  """;
		intervalCommand.Parameters.AddWithValue("scheduleVersionIds", scheduleVersionIds.ToArray());
		_ = await intervalCommand.ExecuteNonQueryAsync();
	}

	/// <summary>
	///     A 3-edge <c>user_cost_rate</c> timeline per worker crossing the staircase window, forcing at
	///     least one rate-boundary split inside every worker's sessions (plan §4).
	/// </summary>
	private static async Task InsertRateTimelineAsync(NpgsqlConnection connection, long[] workerIds, DateTimeOffset windowStart,
		DateTimeOffset windowEnd)
	{
		var span = windowEnd - windowStart;
		var edge1 = windowStart + TimeSpan.FromTicks(span.Ticks / 3);
		var edge2 = windowStart + TimeSpan.FromTicks(span.Ticks * 2 / 3);

		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO user_cost_rate (user_id, effective_start, effective_end, rate, changed_at)
							  SELECT u, edge.effective_start, edge.effective_end, edge.rate, now()
							  FROM unnest(@workerIds) AS u
							  CROSS JOIN (VALUES
							      (@windowStart, @edge1, @rate1),
							      (@edge1, @edge2, @rate2),
							      (@edge2, NULL, @rate3)
							  ) AS edge (effective_start, effective_end, rate);
							  """;
		command.Parameters.AddWithValue("workerIds", workerIds);
		command.Parameters.AddWithValue("windowStart", windowStart);
		command.Parameters.AddWithValue("edge1", edge1);
		command.Parameters.AddWithValue("edge2", edge2);
		command.Parameters.AddWithValue("rate1", OverlapDefaultHourlyRate);
		command.Parameters.AddWithValue("rate2", OverlapDefaultHourlyRate + OverlapRateEdgeStep);
		command.Parameters.AddWithValue("rate3", OverlapDefaultHourlyRate + (OverlapRateEdgeStep * 2));
		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task<long> QueryFirstLeafIdAsync(NpgsqlConnection connection, long branchId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT jn.id
							  FROM job_node jn
							  JOIN leaf_work lw ON lw.job_node_id = jn.id
							  WHERE jn.parent_id = @branchId
							  ORDER BY jn.id
							  LIMIT 1;
							  """;
		command.Parameters.AddWithValue("branchId", branchId);
		return (long)(await command.ExecuteScalarAsync())!;
	}
}

/// <summary>
///     Anchor ids and a human-readable, fully deterministic parameter record ("seed") for a generated
///     overlapping-cost scale (plan §5) -- <see cref="OwnerActorId" /> is the <c>app_user</c> id owning
///     <see cref="OneBranchId" />/<see cref="OneLeafId" />, not a permission-bearing actor; the performance
///     test seeds its own cost-viewing identity separately. <see cref="HeavyWorkerId" />/
///     <see cref="HeavyWorkerBranchId" /> are populated only when the scale was seeded with the optional
///     heavy-worker addendum (plan §4).
/// </summary>
public sealed record OverlappingCostScaleSeed(
	long OwnerActorId,
	long OneLeafId,
	long OneBranchId,
	DateTimeOffset AsOf,
	string Seed,
	long? HeavyWorkerId,
	long? HeavyWorkerBranchId);
