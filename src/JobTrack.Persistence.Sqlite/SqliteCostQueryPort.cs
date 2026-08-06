namespace JobTrack.Persistence.Sqlite;

using System.Data;
using Abstractions;
using Application;
using Application.Ports;
using Domain.Costing;
using Domain.Hierarchy;
using Domain.Intervals;
using Domain.Rates;
using Domain.Schedules;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using Shared;
using Shared.Entities;

/// <summary>
///     SQLite implementation of <see cref="ICostQueryPort" /> (impl plan §7.3/§7.4 slice 10: calculate
///     cost details and hierarchy totals). One <see cref="SqliteJobTrackDbContext" /> per call,
///     read-only throughout. Materializes only the requested subtree(s) and each contributing worker's
///     sessions/schedules/exceptions/overrides/rates bounded to the costed window (2026-07-24
///     code-review-scalability-remediation-plan §2.2) -- never the whole <c>job_node</c> table -- while
///     still honoring ADR 0017's elevated read scope (a contributing worker's sessions can be on any
///     leaf, not only the requested subtree, for a correct concurrency divisor) by extending the loaded
///     node/owner maps with exactly the ancestor chains that scope needs (<see cref="CostQueryAssembly.ExtendAncestryAsync" />).
///     Leaves every authorization decision and the actual cost calculation to <see cref="CostQueries" />
///     and the pure domain engine. Schedule expansion (<see cref="ScheduleExpander" />) and exception
///     resolution (<see cref="ScheduleExceptionResolver" />) are explicitly domain, not schema-layer,
///     concerns, so this port calls them itself over the raw historical schedule rows.
/// </summary>
internal sealed class SqliteCostQueryPort : ICostQueryPort
{
	private readonly IClock clock;
	private readonly string connectionString;
	private readonly IReadOnlyList<IInterceptor> interceptors = [];

	/// <summary>Creates the port over the given SQLite connection string.</summary>
	public SqliteCostQueryPort(string connectionString, IClock clock)
	{
		this.connectionString = connectionString;
		this.clock = clock;
	}

	/// <summary>Test-only seam for asserting bulk-query command and connection bounds.</summary>
	internal SqliteCostQueryPort(
		string connectionString, IClock clock, IReadOnlyList<IInterceptor> interceptors)
		: this(connectionString, clock) =>
		this.interceptors = interceptors;

	/// <inheritdoc />
	public async Task<CostAccessInputs> GetCostAccessInputsAsync(
		AppUserId actorId, JobNodeId nodeId, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(
			IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
		var actorRoles = await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);
		var ownerIds = await JobNodeHierarchyQueries.GetAncestorOwnerIdsAsync(context, nodeId.Value, cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return new() { ActorRoles = actorRoles, AncestorOwnerIds = EquatableArray.CopyOf(ownerIds.Select(id => new AppUserId(id))) };
	}

	/// <inheritdoc />
	public async Task<CostQueryResult> GetCostInputsAsync(
		JobNodeId nodeId, Instant asOf, int maxHierarchyNodes, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await SqliteCostQuerySnapshot.BeginAsync(context, cancellationToken).ConfigureAwait(false);

		var subtree = await CostQueryAssembly.LoadSubtreeAsync(context, [nodeId], cancellationToken).ConfigureAwait(false);
		if (!subtree.ExistingRootIds.Contains(nodeId)) {
			throw new EntityNotFoundException($"Job node {nodeId} does not exist.");
		}

		if (subtree.NodesById.Count > maxHierarchyNodes) {
			throw new ArgumentOutOfRangeException(
				nameof(maxHierarchyNodes),
				subtree.NodesById.Count,
				$"This node's subtree has {subtree.NodesById.Count} nodes, exceeding the {maxHierarchyNodes}-node maximum. Query a smaller subtree.");
		}

		var (bounds, workers) = await CostQueryAssembly.LoadWorkersAndExtendAncestryAsync(
			context, subtree, asOf, cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		return new() { NodesById = EquatableDictionaryFactory.CopyOf(subtree.NodesById), Bounds = bounds, Workers = EquatableArray.CopyOf(workers) };
	}

	/// <inheritdoc />
	public async Task<BulkCostQueryResult> GetBulkCostInputsAsync(
		AppUserId actorId, EquatableArray<JobNodeId> nodeIds, Instant asOf, int maxHierarchyNodes, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await SqliteCostQuerySnapshot.BeginAsync(context, cancellationToken).ConfigureAwait(false);

		var actorRoles = await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);

		var subtree = await CostQueryAssembly.LoadSubtreeAsync(context, nodeIds, cancellationToken).ConfigureAwait(false);

		if (subtree.NodesById.Count > maxHierarchyNodes) {
			throw new ArgumentOutOfRangeException(
				nameof(maxHierarchyNodes),
				subtree.NodesById.Count,
				$"These nodes' combined subtrees have {subtree.NodesById.Count} nodes, exceeding the {maxHierarchyNodes}-node maximum.");
		}

		var (bounds, workers) = await CostQueryAssembly.LoadWorkersAndExtendAncestryAsync(
			context, subtree, asOf, cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		return new() {
			ActorRoles = actorRoles,
			NodesById = EquatableDictionaryFactory.CopyOf(subtree.NodesById),
			OwnerUserIdsById = EquatableDictionaryFactory.CopyOf(subtree.OwnersById),
			Bounds = bounds,
			Workers = EquatableArray.CopyOf(workers),
		};
	}

	private SqliteJobTrackDbContext CreateContext() => SqliteDbContextFactory.CreateContext(connectionString, interceptors);

	private async Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		SqliteJobTrackDbContext context, AppUserId actorId, CancellationToken cancellationToken)
	{
		var actorIdentityUser = await context.Set<IdentityUserEntity>().AsNoTracking()
									.FirstOrDefaultAsync(iu => iu.AppUserId == actorId, cancellationToken).ConfigureAwait(false)
								?? throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		ActorAccountState.EnsureMayAct(actorIdentityUser, actorId, clock.GetCurrentInstant());

		var roles = await context.Set<IdentityUserRoleEntity>().AsNoTracking()
			.Where(ur => ur.IdentityUserId == actorIdentityUser.Id)
			.Select(ur => (EmployeeRole)ur.IdentityRoleId)
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);

		return [.. roles];
	}
}

/// <summary>
///     The cost-input assembly logic behind <see cref="SqliteCostQueryPort" />, mirrored (necessarily
///     duplicated, not literally shared) by PostgreSQL's own <c>CostQueryAssembly</c>: both operate
///     against already-converted, provider-normalized entity values (each provider's own
///     <c>DbContext</c> applies its own <see cref="Instant" />/<see cref="HourlyRate" /> conversions
///     before these rows are ever read), so the in-memory assembly into <see cref="CostQueryResult" />
///     is identical regardless of provider. It cannot live in <c>JobTrack.Persistence.Shared</c>,
///     which is deliberately scoped to <c>JobTrack.Abstractions</c> only (impl plan §7.4 project
///     layout) and does not reference <c>JobTrack.Domain</c>/<c>JobTrack.Application</c> -- the same
///     constraint every other provider-pair port under this slice already accepts.
/// </summary>
internal static class CostQueryAssembly
{
	/// <summary>
	///     Loads exactly <paramref name="rootIds" />' own subtrees (2026-07-24
	///     code-review-scalability-remediation-plan §2.2 step 2) through a set-based parameterized
	///     recursive query, rather than the whole <c>job_node</c> table -- SQLite has no composable
	///     stored set-returning function, so the recursive query stays here as a minimal parameterized
	///     statement rather than leaking into the shared persistence layer (mirroring
	///     <c>SqliteControlledLeafQuery</c>'s own established pattern). A requested root absent from
	///     <see cref="SubtreeLoad.ExistingRootIds" /> does not exist.
	/// </summary>
	public static async Task<SubtreeLoad> LoadSubtreeAsync(
		DbContext context, IReadOnlyCollection<JobNodeId> rootIds, CancellationToken cancellationToken)
	{
		var rootIdValues = rootIds.Select(id => id.Value).ToList();
		if (rootIdValues.Count == 0) {
			return new([], [], []);
		}

		var rootIdParameters = rootIdValues.Select((_, index) => $"@rootId{index}").ToArray();
		var sql = $"""
				   WITH RECURSIVE subtree(origin_root_id, id) AS (
				       SELECT id, id FROM job_node WHERE id IN ({string.Join(',', rootIdParameters)})
				       UNION ALL
				       SELECT s.origin_root_id, jn.id
				       FROM job_node jn
				       JOIN subtree s ON jn.parent_id = s.id
				   )
				   SELECT DISTINCT s.origin_root_id AS "OriginRootId", s.id AS "Id", jn.parent_id AS "ParentId",
				          jn.owner_user_id AS "OwnerUserId", lw.achievement_id AS "AchievementId"
				   FROM subtree s
				   JOIN job_node jn ON jn.id = s.id
				   LEFT JOIN leaf_work lw ON lw.job_node_id = s.id
				   """;
		var parameters = rootIdValues.Select((rootId, index) => (object)new SqliteParameter(rootIdParameters[index], rootId)).ToArray();
		var rows = await context.Database.SqlQueryRaw<SubtreeRow>(sql, parameters)
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		var existingRootIds = new HashSet<JobNodeId>(
			rows.Where(row => row.Id == row.OriginRootId).Select(row => new JobNodeId(row.Id)));

		var distinctRows = rows.GroupBy(row => row.Id).Select(group => group.First()).ToList();
		var childrenByParent = distinctRows
			.Where(row => row.ParentId is not null)
			.GroupBy(row => row.ParentId!.Value)
			.ToDictionary(group => group.Key, group => EquatableArray.CopyOf(group.Select(row => new JobNodeId(row.Id))));

		var nodesById = distinctRows.ToDictionary(
			row => new JobNodeId(row.Id),
			row => new HierarchyNode(
				new(row.Id),
				row.ParentId is long parentId ? new JobNodeId(parentId) : null,
				childrenByParent.TryGetValue(row.Id, out var children) ? children : [],
				row.AchievementId is short achievementId ? (Achievement)achievementId : null));
		var ownersById = distinctRows.ToDictionary(
			row => new JobNodeId(row.Id), row => row.OwnerUserId is long ownerUserId ? new AppUserId(ownerUserId) : (AppUserId?)null);

		return new(nodesById, ownersById, existingRootIds);
	}

	/// <summary>
	///     Loads every contributing worker's cost inputs for <paramref name="subtree" />'s requested
	///     node set, then extends <paramref name="subtree" />'s node/owner maps in place with every
	///     ancestor-chain node needed above it: each requested root's own path to the true root (a rate
	///     override can be declared above the requested subtree; ADR 0040's owner carve-out walk needs
	///     it too) and, for any contributing session on a leaf outside the requested subtree (ADR 0017's
	///     elevated read scope), that leaf's own path to the root -- <see cref="Domain.Rates.RateResolver" />
	///     walks every session's own node upward looking for the nearest override.
	/// </summary>
	public static async Task<(WorkInterval Bounds, List<WorkerCostInputs> Workers)> LoadWorkersAndExtendAncestryAsync(
		DbContext context, SubtreeLoad subtree, Instant asOf, CancellationToken cancellationToken)
	{
		var requestedNodeIds = subtree.NodesById.Keys.ToArray();
		// One grouped query replaces what were previously a separate MIN and DISTINCT over the
		// identical filter (2026-08-06-cost-read-materialisation-reduction-plan.md Stage 2): each
		// worker's own earliest requested-session start is the per-group aggregate, and the query's
		// earliest overall start (needed for `bounds`) is the cheap client-side minimum over the
		// resulting per-worker rows -- at most a few dozen, not one per session.
		var perWorkerEarliestStarts = await context.Set<WorkSessionEntity>().AsNoTracking()
			.Where(s => requestedNodeIds.Contains(s.LeafWorkId) && s.StartedAt < asOf)
			.GroupBy(s => s.WorkedByUserId)
			.Select(group => new { WorkerId = group.Key, EarliestStart = group.Min(s => s.StartedAt) })
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		var bounds = new WorkInterval(Instant.MinValue, asOf);
		var workers = new List<WorkerCostInputs>();
		var workerIds = new List<AppUserId>();
		if (perWorkerEarliestStarts.Count > 0) {
			bounds = new(perWorkerEarliestStarts.Min(row => row.EarliestStart), asOf);
			workerIds = perWorkerEarliestStarts.Select(row => row.WorkerId).ToList();
			var sessions = await context.Set<WorkSessionEntity>().AsNoTracking()
				.Where(s => workerIds.Contains(s.WorkedByUserId)
							&& s.StartedAt < bounds.End && (s.FinishedAt == null || s.FinishedAt > bounds.Start))
				.ToListAsync(cancellationToken).ConfigureAwait(false);

			// 2026-07-25 scalability-follow-up plan §2.5: a schedule version's own EffectiveStart/End
			// are civil LocalDates in its own IanaTimeZone, not directly comparable to the Instant-based
			// bounds -- but any zone's offset is under 24h, so a one-day-widened UTC-date window (the
			// same slack ScheduleExpander.Expand already tolerates for midnight-crossing/DST shifts) is a
			// safe, portable, provider-agnostic prefilter: it cannot exclude a version that could
			// actually produce a working interval inside bounds, since ScheduleExpander clips exactly
			// per-zone downstream regardless.
			var widenedStart = bounds.Start.InZone(DateTimeZone.Utc).Date.PlusDays(-1);
			var widenedEnd = bounds.End.InZone(DateTimeZone.Utc).Date.PlusDays(1);
			var scheduleVersions = await context.Set<ScheduleVersionEntity>().AsNoTracking()
				.Where(v => workerIds.Contains(v.UserId) && v.EffectiveStart < widenedEnd
														 && (v.EffectiveEnd == null || v.EffectiveEnd > widenedStart))
				.ToListAsync(cancellationToken).ConfigureAwait(false);
			var scheduleVersionIds = scheduleVersions.Select(v => v.Id).ToList();
			var scheduleIntervals = await context.Set<ScheduleIntervalEntity>().AsNoTracking()
				.Where(i => scheduleVersionIds.Contains(i.ScheduleVersionId)).ToListAsync(cancellationToken).ConfigureAwait(false);
			var exceptions = await context.Set<ScheduleExceptionEntity>().AsNoTracking()
				.Where(e => workerIds.Contains(e.UserId) && e.StartedAt < bounds.End && e.FinishedAt > bounds.Start)
				.ToListAsync(cancellationToken).ConfigureAwait(false);
			var userCostRates = await context.Set<UserCostRateEntity>().AsNoTracking()
				.Where(r => workerIds.Contains(r.UserId) && r.EffectiveStart < bounds.End
														 && (r.EffectiveEnd == null || r.EffectiveEnd > bounds.Start))
				.ToListAsync(cancellationToken).ConfigureAwait(false);
			var appUsersById = await context.Set<AppUserEntity>().AsNoTracking()
				.Where(u => workerIds.Contains(u.Id))
				.ToDictionaryAsync(u => u.Id, cancellationToken).ConfigureAwait(false);

			var intervalsByVersion = scheduleIntervals.GroupBy(i => i.ScheduleVersionId).ToDictionary(group => group.Key, group => group.ToList());
			var sessionsByWorker = sessions.ToLookup(session => session.WorkedByUserId);
			var versionsByWorker = scheduleVersions.ToLookup(version => version.UserId);
			var exceptionsByWorker = exceptions.ToLookup(exception => exception.UserId);
			var ratesByWorker = userCostRates.ToLookup(rate => rate.UserId);

			foreach (var workerId in workerIds) {
				var workerSessions = sessionsByWorker[workerId]
					.Select(s => new CostableSession(s.Id, s.LeafWorkId, new(s.StartedAt, SessionEndClipping.ClipEnd(s.FinishedAt, asOf))))
					.ToArray();

				var expandedScheduleIntervals = new List<WorkInterval>();
				foreach (var version in versionsByWorker[workerId]) {
					var weeklyIntervals = intervalsByVersion.GetValueOrDefault(version.Id, [])
						.Select(i => new WeeklyInterval(i.DayOfWeek, i.StartTime, i.EndTime));
					var scheduleVersion = new ScheduleVersion(
						StoredTimeZoneResolver.Resolve(version.IanaTimeZone, $"Schedule version {version.Id}"),
						version.EffectiveStart, version.EffectiveEnd,
						EquatableArray.CopyOf(weeklyIntervals));
					expandedScheduleIntervals.AddRange(ScheduleExpander.Expand(scheduleVersion, bounds));
				}

				var workerExceptions = exceptionsByWorker[workerId]
					.Select(e => new ScheduleExceptionEntry(
						(ScheduleExceptionEffect)e.ScheduleExceptionEffectId, new(e.StartedAt, e.FinishedAt), e.RateOverride))
					.ToArray();

				var normalizedScheduled = IntervalAlgebra.Normalize(expandedScheduleIntervals);
				var effectiveWorkingIntervals = ScheduleExceptionResolver.Apply(expandedScheduleIntervals, workerExceptions);

				var workerUserCostRates = ratesByWorker[workerId]
					.Select(r => new UserCostRate(r.Rate, r.EffectiveStart, r.EffectiveEnd))
					.ToArray();

				// NodeOverrides is filled in below, once ExtendAncestryAsync has determined the final
				// node set (2026-07-25 scalability-follow-up plan §2.5: an override on a node outside
				// that set can never be consulted by RateResolver, which only walks a session's own node
				// and its ancestors -- see NodeOverrides' own remarks).
				workers.Add(new() {
					Sessions = EquatableArray.CopyOf(workerSessions),
					EffectiveWorkingIntervals = EquatableArray.CopyOf(effectiveWorkingIntervals),
					ScheduledWorkingIntervals = EquatableArray.CopyOf(normalizedScheduled),
					Exceptions = EquatableArray.CopyOf(workerExceptions),
					NodeOverrides = [],
					UserCostRates = EquatableArray.CopyOf(workerUserCostRates),
					UserDefaultRate = appUsersById.TryGetValue(workerId, out var appUser) ? appUser.DefaultHourlyRate : null,
				});
			}
		}

		await ExtendAncestryAsync(context, subtree, workers, cancellationToken).ConfigureAwait(false);

		if (workers.Count > 0) {
			// Only nodes actually reachable from a session's own ancestor walk (subtree.NodesById, now
			// fully extended) can ever be consulted by RateResolver, so this is safe to filter by node
			// id as well as by time window and worker, unlike UserCostRates (user-wide, not node-scoped).
			var finalNodeIds = subtree.NodesById.Keys.ToArray();
			var nodeOverrides = await context.Set<NodeRateOverrideEntity>().AsNoTracking()
				.Where(o => workerIds.Contains(o.UserId) && finalNodeIds.Contains(o.NodeId)
														 && o.EffectiveStart < bounds.End &&
														 (o.EffectiveEnd == null || o.EffectiveEnd > bounds.Start))
				.ToListAsync(cancellationToken).ConfigureAwait(false);
			var overridesByWorker = nodeOverrides.ToLookup(overrideEntry => overrideEntry.UserId);

			for (var index = 0; index < workers.Count; ++index) {
				var workerNodeOverrides = overridesByWorker[workerIds[index]]
					.Select(o => new NodeRateOverride(o.NodeId, o.Rate, o.EffectiveStart, o.EffectiveEnd))
					.ToArray();
				workers[index] = workers[index] with { NodeOverrides = EquatableArray.CopyOf(workerNodeOverrides) };
			}
		}

		return (bounds, workers);
	}

	private static async Task ExtendAncestryAsync(
		DbContext context, SubtreeLoad subtree, IReadOnlyList<WorkerCostInputs> workers, CancellationToken cancellationToken)
	{
		var missingIds = new HashSet<JobNodeId>(subtree.ExistingRootIds);
		foreach (var session in workers.SelectMany(worker => worker.Sessions)) {
			if (!subtree.NodesById.ContainsKey(session.NodeId)) {
				_ = missingIds.Add(session.NodeId);
			}
		}

		if (missingIds.Count == 0) {
			return;
		}

		var missingIdValues = missingIds.Select(id => id.Value).ToList();
		var missingIdParameters = missingIdValues.Select((_, index) => $"@leafId{index}").ToArray();
		var sql = $"""
				   WITH RECURSIVE ancestors(id, parent_id, owner_user_id) AS (
				       SELECT id, parent_id, owner_user_id FROM job_node WHERE id IN ({string.Join(',', missingIdParameters)})
				       UNION ALL
				       SELECT jn.id, jn.parent_id, jn.owner_user_id
				       FROM job_node jn
				       JOIN ancestors a ON jn.id = a.parent_id
				   )
				   SELECT DISTINCT id AS "Id", parent_id AS "ParentId", owner_user_id AS "OwnerUserId" FROM ancestors
				   """;
		var parameters = missingIdValues.Select((leafId, index) => (object)new SqliteParameter(missingIdParameters[index], leafId)).ToArray();
		var ancestorRows = await context.Database.SqlQueryRaw<AncestorRow>(sql, parameters)
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		foreach (var row in ancestorRows) {
			var id = new JobNodeId(row.Id);
			if (subtree.NodesById.ContainsKey(id)) {
				continue;
			}

			subtree.NodesById[id] = new(id, row.ParentId is long parentId ? new JobNodeId(parentId) : null, [], null);
			subtree.OwnersById[id] = row.OwnerUserId is long ownerUserId ? new AppUserId(ownerUserId) : null;
		}
	}
}

/// <summary>
///     <see cref="CostQueryAssembly.LoadSubtreeAsync" />'s materialized result: the requested roots'
///     own subtrees only, plus which of the requested root ids actually exist. A plain class, not a
///     record -- <see cref="CostQueryAssembly.ExtendAncestryAsync" /> mutates <see cref="NodesById" />/
///     <see cref="OwnersById" /> in place, so this deliberately carries no value-equality semantics.
/// </summary>
internal sealed class SubtreeLoad(
	Dictionary<JobNodeId, HierarchyNode> nodesById,
	Dictionary<JobNodeId, AppUserId?> ownersById,
	HashSet<JobNodeId> existingRootIds)
{
	public Dictionary<JobNodeId, HierarchyNode> NodesById { get; } = nodesById;

	public Dictionary<JobNodeId, AppUserId?> OwnersById { get; } = ownersById;

	public HashSet<JobNodeId> ExistingRootIds { get; } = existingRootIds;
}

/// <summary>One row of <see cref="CostQueryAssembly.LoadSubtreeAsync" />'s recursive subtree query.</summary>
internal sealed record SubtreeRow(long OriginRootId, long Id, long? ParentId, long? OwnerUserId, short? AchievementId);

/// <summary>One row of <see cref="CostQueryAssembly.ExtendAncestryAsync" />'s recursive ancestor-chain query.</summary>
internal sealed record AncestorRow(long Id, long? ParentId, long? OwnerUserId);
