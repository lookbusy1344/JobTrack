namespace JobTrack.TestSupport;

using Abstractions;
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

	// Long-history scale (docs/traceability/performance-budgets.md §1 "Long history").
	private const int LongHistoryDefaultWorkerCount = 20;
	private const int LongHistoryDefaultDays = 5 * 365;
	private const decimal LongHistoryDefaultHourlyRate = 18.00m;
	private const int LongHistorySessionStartHour = 9;
	private const int LongHistorySessionDurationHours = 1;
	private const int LongHistoryExceptionStartHour = 12;
	private const int LongHistoryExceptionDurationHours = 1;

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

	// Npgsql's 30 s default Command Timeout was never a deliberate budget for this generator's bulk
	// seeding statements (tens of thousands of rows per scale) -- it's just the untouched default,
	// and a shared local Postgres instance under load from other test projects in the same `dotnet
	// test JobTrack.slnx` run can push a single batch past it even though the whole seed normally
	// completes in well under a minute. This is setup plumbing, not a measured quantity, so widening
	// it doesn't weaken any regression guard.
	private const int SeedCommandTimeoutSeconds = 120;
	private static readonly TimeSpan OverlapSlotDuration = TimeSpan.FromHours(1);

	/// <summary>
	///     Opens a connection for scale-generation seeding with <see cref="SeedCommandTimeoutSeconds" />
	///     as every command's default <c>CommandTimeout</c>, rather than each performance-test class's
	///     own connection using Npgsql's 30 s default.
	/// </summary>
	public static async Task<NpgsqlConnection> OpenConnectionForSeedingAsync(string connectionString)
	{
		var seedingConnectionString =
			new NpgsqlConnectionStringBuilder(connectionString) { CommandTimeout = SeedCommandTimeoutSeconds }.ConnectionString;
		var connection = new NpgsqlConnection(seedingConnectionString);
		await connection.OpenAsync();
		return connection;
	}

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
		for (var level = 0; level < 9; ++level) {
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

	/// <summary>
	///     The same combined production tree as <see cref="SeedCombinedProductionTreeAsync" />, except
	///     all but every <paramref name="unfinishedEveryNth" />th leaf is marked <c>Success</c> --
	///     a mature installation's realistic completion ratio, as opposed to every leaf starting
	///     <c>Waiting</c> (2026-07-24 code-review-scalability-remediation-plan §2.2 step 4: the
	///     all-<c>Waiting</c> fixture cannot exercise Awaiting Progress's narrowed load at all, since
	///     every leaf legitimately still belongs on the list).
	/// </summary>
	public static async Task<(long RootId, long BranchId, long LeafId, long DeepNodeId)> SeedCombinedProductionTreeMostlyFinishedAsync(
		NpgsqlConnection connection, long ownerUserId, int unfinishedEveryNth = 50)
	{
		var tree = await SeedCombinedProductionTreeAsync(connection, ownerUserId);

		var finishableLeafIds = new List<long>();
		await using (var selectCommand = connection.CreateCommand()) {
			selectCommand.CommandText = """
										SELECT jn.id FROM job_node jn
										WHERE NOT EXISTS (SELECT 1 FROM job_node c WHERE c.parent_id = jn.id)
										AND jn.id % @unfinishedEveryNth <> 0;
										""";
			selectCommand.Parameters.AddWithValue("unfinishedEveryNth", unfinishedEveryNth);
			await using var reader = await selectCommand.ExecuteReaderAsync();
			while (await reader.ReadAsync()) {
				finishableLeafIds.Add(reader.GetInt64(0));
			}
		}

		// Batched the same way every bulk insert above is (this class's own header comment): one
		// UPDATE touching all ~180,000 leaves at once exhausts the shared lock-table pool the same way
		// a single oversized INSERT would.
		for (var offset = 0; offset < finishableLeafIds.Count; offset += LockSafeBatchSize) {
			var batch = finishableLeafIds.GetRange(offset, Math.Min(LockSafeBatchSize, finishableLeafIds.Count - offset));

			await using var updateCommand = connection.CreateCommand();
			updateCommand.CommandText = "UPDATE leaf_work SET achievement_id = @successAchievementId WHERE job_node_id = ANY(@leafIds);";
			updateCommand.Parameters.AddWithValue("successAchievementId", (short)Achievement.Success);
			updateCommand.Parameters.AddWithValue("leafIds", batch.ToArray());
			_ = await updateCommand.ExecuteNonQueryAsync();
		}

		await using var analyzeCommand = connection.CreateCommand();
		analyzeCommand.CommandText = "ANALYZE leaf_work;";
		_ = await analyzeCommand.ExecuteNonQueryAsync();

		return tree;
	}

	/// <summary>
	///     §2.2 of the 2026-07-28 fresh-eyes review: one non-trivial required branch (a small subtree
	///     with an unfinished leaf, so the branch itself never succeeds), plus <paramref name="dependentCount" />
	///     separate leaves each declaring their own direct prerequisite on that branch -- the fan-out
	///     shape <c>job_node_blocked</c>'s original per-edge query repeated the same recursive achievement
	///     traversal for. A realistic mix of finished/unfinished candidates (every
	///     <paramref name="finishedEveryNth" />th dependent is <c>Success</c>, the rest <c>Waiting</c>) so
	///     the fixture exercises both branches of Awaiting Progress's blocked/unblocked candidate split.
	///     Returns (root id, required branch id, dependent leaf ids).
	/// </summary>
	public static async Task<(long RootId, long RequiredBranchId, long[] DependentLeafIds)> SeedPrerequisiteFanOutAsync(
		NpgsqlConnection connection, long ownerUserId, int dependentCount = 5_000, int finishedEveryNth = 3)
	{
		await using var rootCommand = connection.CreateCommand();
		rootCommand.CommandText = """
								  INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
								  VALUES (NULL, 'Fan-out root', @ownerUserId, @ownerUserId, @priorityId, now())
								  RETURNING id;
								  """;
		rootCommand.Parameters.AddWithValue("ownerUserId", ownerUserId);
		rootCommand.Parameters.AddWithValue("priorityId", PriorityMedium);
		var rootId = (long)(await rootCommand.ExecuteScalarAsync())!;

		await using var requiredBranchCommand = connection.CreateCommand();
		requiredBranchCommand.CommandText = """
											INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
											VALUES (@rootId, 'Fan-out required branch', @ownerUserId, @ownerUserId, @priorityId, now())
											RETURNING id;
											""";
		requiredBranchCommand.Parameters.AddWithValue("rootId", rootId);
		requiredBranchCommand.Parameters.AddWithValue("ownerUserId", ownerUserId);
		requiredBranchCommand.Parameters.AddWithValue("priorityId", PriorityMedium);
		var requiredBranchId = (long)(await requiredBranchCommand.ExecuteScalarAsync())!;

		// A non-trivial required subtree (two child leaves, one of them never finishing), rather than
		// the branch itself carrying LeafWork, so job_node_blocked's recursive node_succeeded genuinely
		// has to descend the required subtree once per distinct required job, not just check one row.
		var requiredLeafIds = await InsertLevelAsync(connection, [requiredBranchId], 2, ownerUserId);
		await InsertLeafWorkInBatchesAsync(connection, requiredLeafIds);
		await using (var finishOneCommand = connection.CreateCommand()) {
			finishOneCommand.CommandText = "UPDATE leaf_work SET achievement_id = @successAchievementId WHERE job_node_id = @leafId;";
			finishOneCommand.Parameters.AddWithValue("successAchievementId", (short)Achievement.Success);
			finishOneCommand.Parameters.AddWithValue("leafId", requiredLeafIds[0]);
			_ = await finishOneCommand.ExecuteNonQueryAsync();
		}

		var dependentLeafIds = await InsertLevelAsync(connection, [rootId], dependentCount, ownerUserId);
		await InsertLeafWorkInBatchesAsync(connection, dependentLeafIds);

		var finishableDependentIds = dependentLeafIds.Where((_, index) => (index + 1) % finishedEveryNth == 0).ToArray();
		for (var offset = 0; offset < finishableDependentIds.Length; offset += LockSafeBatchSize) {
			var batch = finishableDependentIds[offset..Math.Min(offset + LockSafeBatchSize, finishableDependentIds.Length)];

			await using var finishCommand = connection.CreateCommand();
			finishCommand.CommandText = "UPDATE leaf_work SET achievement_id = @successAchievementId WHERE job_node_id = ANY(@leafIds);";
			finishCommand.Parameters.AddWithValue("successAchievementId", (short)Achievement.Success);
			finishCommand.Parameters.AddWithValue("leafIds", batch);
			_ = await finishCommand.ExecuteNonQueryAsync();
		}

		for (var offset = 0; offset < dependentLeafIds.Length; offset += LockSafeBatchSize) {
			var batch = dependentLeafIds[offset..Math.Min(offset + LockSafeBatchSize, dependentLeafIds.Length)];

			await using var prerequisiteCommand = connection.CreateCommand();
			prerequisiteCommand.CommandText = """
											  INSERT INTO job_prerequisite (from_id, to_id)
											  SELECT @requiredBranchId, dependent_id FROM unnest(@dependentIds) AS dependent_id;
											  """;
			prerequisiteCommand.Parameters.AddWithValue("requiredBranchId", requiredBranchId);
			prerequisiteCommand.Parameters.AddWithValue("dependentIds", batch);
			_ = await prerequisiteCommand.ExecuteNonQueryAsync();
		}

		await using (var analyzeCommand = connection.CreateCommand()) {
			analyzeCommand.CommandText = "ANALYZE job_node; ANALYZE leaf_work; ANALYZE job_prerequisite;";
			_ = await analyzeCommand.ExecuteNonQueryAsync();
		}

		return (rootId, requiredBranchId, dependentLeafIds);
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

		var heavyWorkerBranchId = heavyWorkerId.HasValue ? branchIdByWorker[heavyWorkerId.Value] : (long?)null;

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

	/// <summary>
	///     "Long history" (§1): one <c>job_node</c> subtree (one branch, one leaf per worker) with
	///     <paramref name="days" /> days of daily <c>work_session</c> rows for
	///     <paramref name="workerCount" /> workers (≈36,500 sessions at the defaults, one leaf-day per
	///     worker) plus the same number of daily <c>user_schedule_exception</c> rows (an unpriced daily
	///     lunch-break <c>RemoveWorkingTime</c> exception per worker, so it never trips the priced-additive
	///     no-overlap exclusion constraint). Each worker has one 24x7 weekly schedule version spanning the
	///     whole window, so <c>ScheduleExpander</c> must expand one interval per calendar day across the
	///     full costed window regardless of how sparse that worker's actual sessions are within it -- the
	///     shape 2026-08-06-cost-read-materialisation-reduction-plan §2.1 measures. Returns the seeded
	///     anchors plus a deterministic seed string (§6.6's reproducibility rule).
	/// </summary>
	public static async Task<LongHistoryScaleSeed> SeedLongHistoryScaleAsync(
		NpgsqlConnection connection,
		DateTimeOffset baseInstant,
		int workerCount = LongHistoryDefaultWorkerCount,
		int days = LongHistoryDefaultDays)
	{
		if (workerCount <= 0) {
			throw new ArgumentOutOfRangeException(nameof(workerCount), workerCount, "Worker count must be positive.");
		}

		if (days <= 0) {
			throw new ArgumentOutOfRangeException(nameof(days), days, "Day count must be positive.");
		}

		var workerIds = await InsertLongHistoryWorkersAsync(connection, workerCount);
		var rootId = await InsertOverlapRootAsync(connection, workerIds[0]);
		var branchId = await InsertLongHistoryBranchAsync(connection, rootId, workerIds[0]);
		var leafIdByWorker = await InsertLongHistoryLeavesAsync(connection, branchId, workerIds);

		await InsertLongHistorySessionsAsync(connection, branchId, baseInstant, days);
		await InsertWeekly24x7SchedulesAsync(connection, workerIds, baseInstant);
		await InsertLongHistoryExceptionsAsync(connection, workerIds, baseInstant, days);

		// A bulk-loaded table has no fresh statistics yet -- this fixture's own setup cost, not a
		// production concern (autovacuum keeps them current as sessions accumulate gradually); see
		// every other scale generator's identical note in this file.
		await using (var analyzeCommand = connection.CreateCommand()) {
			analyzeCommand.CommandText = "ANALYZE work_session; ANALYZE user_schedule_exception; ANALYZE job_node; ANALYZE leaf_work;";
			_ = await analyzeCommand.ExecuteNonQueryAsync();
		}

		var asOf = baseInstant.AddDays(days + 1);
		var seed = $"workers={workerCount};days={days};base={baseInstant:O}";

		return new(workerIds[0], leafIdByWorker[workerIds[0]], branchId, asOf, seed, EquatableArray.CopyOf(workerIds));
	}

	private static async Task<long[]> InsertLongHistoryWorkersAsync(NpgsqlConnection connection, int count)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO app_user (display_name, iana_time_zone, default_hourly_rate)
							  SELECT 'Long-history worker ' || g, 'Europe/London', @defaultHourlyRate
							  FROM generate_series(1, @count) AS g
							  RETURNING id;
							  """;
		command.Parameters.AddWithValue("count", count);
		command.Parameters.AddWithValue("defaultHourlyRate", LongHistoryDefaultHourlyRate);

		var ids = new List<long>();
		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			ids.Add(reader.GetInt64(0));
		}

		return [.. ids];
	}

	private static async Task<long> InsertLongHistoryBranchAsync(NpgsqlConnection connection, long rootId, long ownerUserId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
							  VALUES (@rootId, 'Long history branch', @ownerUserId, @ownerUserId, @priorityId, now())
							  RETURNING id;
							  """;
		command.Parameters.AddWithValue("rootId", rootId);
		command.Parameters.AddWithValue("ownerUserId", ownerUserId);
		command.Parameters.AddWithValue("priorityId", PriorityMedium);
		return (long)(await command.ExecuteScalarAsync())!;
	}

	/// <summary>One leaf per worker under <paramref name="branchId" />, keyed by owner user id.</summary>
	private static async Task<Dictionary<long, long>> InsertLongHistoryLeavesAsync(
		NpgsqlConnection connection, long branchId, long[] workerIds)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  WITH inserted_leaves AS (
							      INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
							      SELECT @branchId, 'Long history leaf', u, u, @priorityId, now()
							      FROM unnest(@workerIds) AS u
							      RETURNING id, owner_user_id
							  ),
							  inserted_leaf_work AS (
							      INSERT INTO leaf_work (job_node_id, changed_at)
							      SELECT id, now() FROM inserted_leaves
							      RETURNING job_node_id
							  )
							  SELECT ilw.job_node_id, il.owner_user_id
							  FROM inserted_leaf_work ilw
							  JOIN inserted_leaves il ON il.id = ilw.job_node_id;
							  """;
		command.Parameters.AddWithValue("branchId", branchId);
		command.Parameters.AddWithValue("priorityId", PriorityMedium);
		command.Parameters.AddWithValue("workerIds", workerIds);

		var leafIdByWorker = new Dictionary<long, long>();
		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			leafIdByWorker[reader.GetInt64(1)] = reader.GetInt64(0);
		}

		return leafIdByWorker;
	}

	/// <summary>
	///     One daily <c>work_session</c> per worker on their own leaf under <paramref name="branchId" />,
	///     for <paramref name="days" /> days starting at <paramref name="baseInstant" />'s date -- a single
	///     set-based statement, since the leaves it references number one per worker (well under
	///     <see cref="LockSafeBatchSize" />) regardless of how many day-rows are generated against them.
	/// </summary>
	private static async Task InsertLongHistorySessionsAsync(
		NpgsqlConnection connection, long branchId, DateTimeOffset baseInstant, int days)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  WITH targets AS (
							      SELECT lw.job_node_id AS leaf_id, jn.owner_user_id AS user_id
							      FROM leaf_work lw
							      JOIN job_node jn ON jn.id = lw.job_node_id
							      WHERE jn.parent_id = @branchId
							  )
							  INSERT INTO work_session (leaf_work_id, worked_by_user_id, started_at, finished_at, changed_at)
							  SELECT t.leaf_id, t.user_id,
							         @baseInstant + make_interval(days => d) + make_interval(hours => @sessionStartHour),
							         @baseInstant + make_interval(days => d) + make_interval(hours => @sessionStartHour + @sessionDurationHours),
							         now()
							  FROM targets t
							  CROSS JOIN generate_series(0, @days - 1) AS d;
							  """;
		command.Parameters.AddWithValue("branchId", branchId);
		command.Parameters.AddWithValue("baseInstant", baseInstant);
		command.Parameters.AddWithValue("sessionStartHour", LongHistorySessionStartHour);
		command.Parameters.AddWithValue("sessionDurationHours", LongHistorySessionDurationHours);
		command.Parameters.AddWithValue("days", days);
		_ = await command.ExecuteNonQueryAsync();
	}

	/// <summary>
	///     One daily unpriced <c>RemoveWorkingTime</c> exception (a lunch break) per worker for
	///     <paramref name="days" /> days -- subtractive and unpriced, so it never engages
	///     <c>user_schedule_exception_no_overlap_priced_additive</c>.
	/// </summary>
	private static async Task InsertLongHistoryExceptionsAsync(
		NpgsqlConnection connection, long[] workerIds, DateTimeOffset baseInstant, int days)
	{
		const short removeWorkingTimeEffectId = 2;

		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO user_schedule_exception
							  	(user_id, started_at, finished_at, effect_id, rate_override, reason, created_by, changed_at)
							  SELECT u, @baseInstant + make_interval(days => d) + make_interval(hours => @exceptionStartHour),
							         @baseInstant + make_interval(days => d) + make_interval(hours => @exceptionStartHour + @exceptionDurationHours),
							         @effectId, NULL, 'Daily lunch break', u, now()
							  FROM unnest(@workerIds) AS u
							  CROSS JOIN generate_series(0, @days - 1) AS d;
							  """;
		command.Parameters.AddWithValue("workerIds", workerIds);
		command.Parameters.AddWithValue("baseInstant", baseInstant);
		command.Parameters.AddWithValue("exceptionStartHour", LongHistoryExceptionStartHour);
		command.Parameters.AddWithValue("exceptionDurationHours", LongHistoryExceptionDurationHours);
		command.Parameters.AddWithValue("effectId", removeWorkingTimeEffectId);
		command.Parameters.AddWithValue("days", days);
		_ = await command.ExecuteNonQueryAsync();
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

/// <summary>
///     Anchor ids and a human-readable, fully deterministic parameter record ("seed") for a generated
///     long-history scale (docs/traceability/performance-budgets.md §1). <see cref="OwnerActorId" />
///     is the first seeded worker, granted <c>CostViewer</c> separately by the performance test that
///     needs it. <see cref="OneLeafId" /> is that same worker's own leaf under <see cref="BranchId" />
///     (the seeded subtree root); <see cref="WorkerIds" /> is every worker for tests that need the
///     full set (e.g. asserting session totals).
/// </summary>
public sealed record LongHistoryScaleSeed(
	long OwnerActorId,
	long OneLeafId,
	long BranchId,
	DateTimeOffset AsOf,
	string Seed,
	EquatableArray<long> WorkerIds);
