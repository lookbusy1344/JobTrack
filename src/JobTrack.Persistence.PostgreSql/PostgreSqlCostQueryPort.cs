namespace JobTrack.Persistence.PostgreSql;

using System.Data;
using Abstractions;
using Application;
using Application.Ports;
using Domain.Costing;
using Domain.Hierarchy;
using Domain.Intervals;
using Domain.Rates;
using Domain.Schedules;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using Npgsql;
using Shared;
using Shared.Entities;

/// <summary>
///     PostgreSQL implementation of <see cref="ICostQueryPort" /> (impl plan §7.3/§7.4 slice 10:
///     calculate cost details and hierarchy totals). One <see cref="PostgreSqlJobTrackDbContext" /> per
///     call, read-only throughout. Materializes only the requested subtree(s) and each contributing
///     worker's sessions/schedules/exceptions/overrides/rates bounded to the costed window (2026-07-24
///     code-review-scalability-remediation-plan §2.2) -- never the whole <c>job_node</c> table -- while
///     still honoring ADR 0017's elevated read scope (a contributing worker's sessions can be on any
///     leaf, not only the requested subtree, for a correct concurrency divisor) by extending the loaded
///     node/owner maps with exactly the ancestor chains that scope needs (<see cref="CostQueryAssembly.ExtendAncestryAsync" />).
///     Leaves every authorization decision and the actual cost calculation to <see cref="CostQueries" />
///     and the pure domain engine. Schedule expansion (<see cref="ScheduleExpander" />) and exception
///     resolution (<see cref="ScheduleExceptionResolver" />) are explicitly domain, not schema-layer,
///     concerns (schema version 0015's header), so this port calls them itself over the raw historical
///     schedule rows.
/// </summary>
internal sealed class PostgreSqlCostQueryPort : ICostQueryPort
{
	private readonly MicrosecondTruncatingClock clock;
	private readonly NpgsqlDataSource dataSource;
	private readonly IReadOnlyList<IInterceptor> interceptors = [];

	/// <summary>Creates the port over the given pooled <see cref="NpgsqlDataSource" />.</summary>
	public PostgreSqlCostQueryPort(NpgsqlDataSource dataSource, IClock clock)
	{
		this.dataSource = dataSource;
		this.clock = new MicrosecondTruncatingClock(clock);
	}

	/// <summary>Test-only seam for asserting bulk-query command and connection bounds.</summary>
	internal PostgreSqlCostQueryPort(
		NpgsqlDataSource dataSource, IClock clock, IReadOnlyList<IInterceptor> interceptors)
		: this(dataSource, clock) =>
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
		return new() {
			ActorRoles = actorRoles,
			AncestorOwnerIds = EquatableArray.CopyOf(ownerIds.Select(id => new AppUserId(id))),
		};
	}

	/// <inheritdoc />
	public async Task<CostQueryResult> GetCostInputsAsync(
		JobNodeId nodeId, Instant asOf, int maxHierarchyNodes, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await PostgreSqlCostQuerySnapshot.BeginAsync(context, cancellationToken).ConfigureAwait(false);

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

		return new() {
			NodesById = EquatableDictionaryFactory.CopyOf(subtree.NodesById),
			Bounds = bounds,
			Workers = EquatableArray.CopyOf(workers),
		};
	}

	/// <inheritdoc />
	public async Task<BulkCostQueryResult> GetBulkCostInputsAsync(
		AppUserId actorId, EquatableArray<JobNodeId> nodeIds, Instant asOf, int maxHierarchyNodes, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await PostgreSqlCostQuerySnapshot.BeginAsync(context, cancellationToken).ConfigureAwait(false);

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

	private PostgreSqlJobTrackDbContext CreateContext()
	{
		var optionsBuilder = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, o => o.UseNodaTime());
		if (interceptors.Count > 0) {
			optionsBuilder = optionsBuilder.AddInterceptors(interceptors);
		}

		return new(optionsBuilder.Options);
	}

	private async Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		PostgreSqlJobTrackDbContext context, AppUserId actorId, CancellationToken cancellationToken)
		=> await ActorAccountState.LoadRolesAsync(
			context, actorId, clock.GetCurrentInstant(), cancellationToken).ConfigureAwait(false);
}

/// <summary>
///     The cost-input assembly logic behind <see cref="PostgreSqlCostQueryPort" />, mirrored
///     (necessarily duplicated, not literally shared) by SQLite's own <c>CostQueryAssembly</c>: both
///     operate against already-converted, provider-normalized entity values (each provider's own
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
	///     code-review-scalability-remediation-plan §2.2 step 2) through the
	///     <c>job_node_subtrees</c> stored function, rather than the whole <c>job_node</c> table. A
	///     requested root absent from <see cref="SubtreeLoad.ExistingRootIds" /> does not exist.
	/// </summary>
	public static async Task<SubtreeLoad> LoadSubtreeAsync(
		DbContext context, IReadOnlyCollection<JobNodeId> rootIds, CancellationToken cancellationToken)
	{
		var rootIdValues = rootIds.Select(id => id.Value).ToArray();
		var rows = await context.Database.SqlQuery<SubtreeRow>(
			$"""
			 SELECT origin_root_id AS "OriginRootId", id AS "Id", parent_id AS "ParentId",
			        owner_user_id AS "OwnerUserId", achievement_id AS "AchievementId"
			 FROM job_node_subtrees({rootIdValues})
			 """).ToListAsync(cancellationToken).ConfigureAwait(false);

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
		// Joins job_node_subtrees(existingRootIds) server-side rather than shipping every
		// already-materialized subtree node id back as an `= ANY(array)` parameter
		// (2026-08-06-cost-read-materialisation-reduction-plan.md Stage 3): the parameter shrinks from
		// O(subtree size) to O(requested root count), and the planner sees a real join instead of an
		// opaque array. Also folds in Stage 2's grouped MIN (a composed `IQueryable<long>.Contains`
		// against the EF-converted LeafWorkId column does not translate -- EF throws
		// InvalidOperationException at query-compile time -- so this is one hand-authored statement,
		// not LINQ composed over a SqlQuery subquery). `DISTINCT id` guards a hypothetical future bulk
		// caller whose overlapping requested roots could otherwise duplicate a shared descendant across
		// multiple `origin_root_id` rows -- immaterial to today's single-root callers, and harmless to
		// MIN either way, but a join that silently assumed single-root shape would be a latent trap.
		var existingRootIdValues = subtree.ExistingRootIds.Select(id => id.Value).ToArray();
		var perWorkerEarliestStarts = await context.Database.SqlQuery<PerWorkerEarliestStartRow>(
			$"""
			 SELECT ws.worked_by_user_id AS "WorkerId", MIN(ws.started_at) AS "EarliestStart"
			 FROM work_session ws
			 JOIN (SELECT DISTINCT id FROM job_node_subtrees({existingRootIdValues})) AS subtree ON subtree.id = ws.leaf_work_id
			 WHERE ws.started_at < {asOf}
			 GROUP BY ws.worked_by_user_id
			 """).ToListAsync(cancellationToken).ConfigureAwait(false);

		var bounds = new WorkInterval(Instant.MinValue, asOf);
		var workers = new List<WorkerCostInputs>();
		var workerIds = new List<AppUserId>();
		if (perWorkerEarliestStarts.Count > 0) {
			bounds = new(perWorkerEarliestStarts.Min(row => row.EarliestStart), asOf);
			workerIds = perWorkerEarliestStarts.Select(row => new AppUserId(row.WorkerId)).ToList();

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
			// Projected to the five columns CostQueryAssembly actually reads (2026-08-06-cost-read-
			// materialisation-reduction-plan.md Stage 5): a wide-vs-narrow raw read of this table at the
			// long-history scale (36,500 rows) measured 50.4 ms vs 9.7 ms -- the one worker-scoped load
			// in this method with a row count large enough for entity-shaping cost to be visible next to
			// the query itself; every other load here stays in the tens of rows.
			var exceptions = await context.Set<ScheduleExceptionEntity>().AsNoTracking()
										  .Where(e => workerIds.Contains(e.UserId) && e.StartedAt < bounds.End && e.FinishedAt > bounds.Start)
										  .Select(e => new
										  {
											  e.UserId,
											  e.ScheduleExceptionEffectId,
											  e.StartedAt,
											  e.FinishedAt,
											  e.RateOverride,
										  })
										  .ToListAsync(cancellationToken).ConfigureAwait(false);
			var userCostRates = await context.Set<UserCostRateEntity>().AsNoTracking()
											 .Where(r => workerIds.Contains(r.UserId) && r.EffectiveStart < bounds.End
																					  && (r.EffectiveEnd == null || r.EffectiveEnd > bounds.Start))
											 .ToListAsync(cancellationToken).ConfigureAwait(false);
			var appUsersById = await context.Set<AppUserEntity>().AsNoTracking()
											.Where(u => workerIds.Contains(u.Id))
											.ToDictionaryAsync(u => u.Id, cancellationToken).ConfigureAwait(false);
			var sessionsByWorkerId = await LoadWorkerSessionsAsync(context, workerIds, bounds, asOf, cancellationToken).ConfigureAwait(false);

			var intervalsByVersion = scheduleIntervals.GroupBy(i => i.ScheduleVersionId).ToDictionary(group => group.Key, group => group.ToList());
			var versionsByWorker = scheduleVersions.ToLookup(version => version.UserId);
			var exceptionsByWorker = exceptions.ToLookup(exception => exception.UserId);
			var ratesByWorker = userCostRates.ToLookup(rate => rate.UserId);

			foreach (var workerId in workerIds) {
				var workerSessions = sessionsByWorkerId.GetValueOrDefault(workerId, []);

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

		var ancestorChainRootIds = await ExtendAncestryAsync(context, subtree, workers, cancellationToken).ConfigureAwait(false);

		if (workers.Count > 0) {
			// Joins job_node_subtrees(existingRootIds) UNION job_node_ancestor_chains(ancestorChainRootIds)
			// server-side -- the exact final node set ExtendAncestryAsync just materialized into
			// subtree.NodesById -- rather than shipping that whole (potentially near-50,000-node) id set
			// back as an `= ANY(array)` parameter (Stage 3, same rationale as the worker-discovery query
			// above). Only nodes actually reachable from a session's own ancestor walk can ever be
			// consulted by RateResolver, so this is safe to filter by node id as well as by time window
			// and worker, unlike UserCostRates (user-wide, not node-scoped).
			var overrideQueryRootIdValues = subtree.ExistingRootIds.Select(id => id.Value).ToArray();
			var workerIdValues = workerIds.Select(id => id.Value).ToArray();
			var nodeOverrideRows = await context.Database.SqlQuery<NodeOverrideRow>(
				$"""
				 SELECT nro.node_id AS "NodeId", nro.user_id AS "UserId", nro.rate AS "Rate",
				        nro.effective_start AS "EffectiveStart", nro.effective_end AS "EffectiveEnd"
				 FROM node_rate_override nro
				 WHERE nro.user_id = ANY({workerIdValues})
				   AND nro.effective_start < {bounds.End}
				   AND (nro.effective_end IS NULL OR nro.effective_end > {bounds.Start})
				   AND nro.node_id IN (
				       SELECT id FROM job_node_subtrees({overrideQueryRootIdValues})
				       UNION
				       SELECT id FROM job_node_ancestor_chains({ancestorChainRootIds})
				   )
				 """).ToListAsync(cancellationToken).ConfigureAwait(false);
			var overridesByWorker = nodeOverrideRows.ToLookup(row => new AppUserId(row.UserId));

			for (var index = 0; index < workers.Count; ++index) {
				var workerNodeOverrides = overridesByWorker[workerIds[index]]
										  .Select(row => new NodeRateOverride(new(row.NodeId), new(row.Rate), row.EffectiveStart, row.EffectiveEnd))
										  .ToArray();
				workers[index] = workers[index] with {
					NodeOverrides = EquatableArray.CopyOf(workerNodeOverrides),
				};
			}
		}

		return (bounds, workers);
	}

	private static async Task<long[]> ExtendAncestryAsync(
		DbContext context, SubtreeLoad subtree, IReadOnlyList<WorkerCostInputs> workers, CancellationToken cancellationToken)
	{
		var missingIds = new HashSet<JobNodeId>(subtree.ExistingRootIds);
		foreach (var session in workers.SelectMany(worker => worker.Sessions)) {
			if (!subtree.NodesById.ContainsKey(session.NodeId)) {
				_ = missingIds.Add(session.NodeId);
			}
		}

		if (missingIds.Count == 0) {
			return [];
		}

		var missingIdValues = missingIds.Select(id => id.Value).ToArray();
		var ancestorRows = await context.Database.SqlQuery<AncestorRow>(
			$"""
			 SELECT id AS "Id", parent_id AS "ParentId", owner_user_id AS "OwnerUserId"
			 FROM job_node_ancestor_chains({missingIdValues})
			 """).ToListAsync(cancellationToken).ConfigureAwait(false);
		foreach (var row in ancestorRows) {
			var id = new JobNodeId(row.Id);
			if (subtree.NodesById.ContainsKey(id)) {
				continue;
			}

			subtree.NodesById[id] = new(id, row.ParentId is long parentId ? new JobNodeId(parentId) : null, [], null);
			subtree.OwnersById[id] = row.OwnerUserId is long ownerUserId ? new AppUserId(ownerUserId) : null;
		}

		return missingIdValues;
	}

	/// <summary>
	///     Loads every contributing worker's database-wide overlapping sessions (ADR 0017's elevated read
	///     scope) through one set-based invocation of the <c>worker_overlapping_sessions</c> stored
	///     function (schema version 0018), rather than one command per worker or a duplicated LINQ
	///     predicate. ADR 0010 names both "database-wide overlap
	///     discovery" and "the canonical cost-input queries" as the sanctioned reason this function
	///     exists, and only a query expressed against the generated <c>session_range</c> column lets the
	///     planner use <c>work_session_user_range_gist_idx</c> instead of filtering the worker's entire
	///     history in memory. <see cref="OverlappingSessionRow.FinishedAt" /> is deliberately not clipped
	///     to <paramref name="asOf" /> by the function itself (its own <c>effective_finished_at</c> column
	///     is not selected here) -- <see cref="Shared.SessionEndClipping.ClipEnd" /> does that, exactly as before this change.
	/// </summary>
	private static async Task<Dictionary<AppUserId, CostableSession[]>> LoadWorkerSessionsAsync(
		DbContext context, IReadOnlyCollection<AppUserId> workerIds, WorkInterval bounds, Instant asOf, CancellationToken cancellationToken)
	{
		var workerIdValues = workerIds.Select(workerId => workerId.Value).ToArray();
		var rows = await context.Database.SqlQuery<OverlappingSessionRow>(
			$"""
			 SELECT worker_ids.worker_id AS "WorkerId", sessions.session_id AS "SessionId", sessions.leaf_work_id AS "LeafWorkId",
			        sessions.started_at AS "StartedAt", sessions.finished_at AS "FinishedAt"
			 FROM unnest({workerIdValues}) AS worker_ids(worker_id)
			 CROSS JOIN LATERAL worker_overlapping_sessions(worker_ids.worker_id, {bounds.Start}, {bounds.End}, {asOf}) AS sessions
			 """).ToListAsync(cancellationToken).ConfigureAwait(false);

		return rows
			   .GroupBy(row => new AppUserId(row.WorkerId))
			   .ToDictionary(
				   group => group.Key,
				   group => group.Select(row => new CostableSession(
									 new(row.SessionId), new(row.LeafWorkId), new(row.StartedAt, SessionEndClipping.ClipEnd(row.FinishedAt, asOf))))
								 .ToArray());
	}
}

/// <summary>One row of <see cref="CostQueryAssembly.LoadWorkerSessionsAsync" />, mapping the set-based <c>worker_overlapping_sessions</c> invocation.</summary>
internal sealed record OverlappingSessionRow(long WorkerId, long SessionId, long LeafWorkId, Instant StartedAt, Instant? FinishedAt);

/// <summary>One row of <see cref="CostQueryAssembly.LoadWorkersAndExtendAncestryAsync" />'s grouped worker-discovery query.</summary>
internal sealed record PerWorkerEarliestStartRow(long WorkerId, Instant EarliestStart);

/// <summary>One row of <see cref="CostQueryAssembly.LoadWorkersAndExtendAncestryAsync" />'s node-override query.</summary>
internal sealed record NodeOverrideRow(long NodeId, long UserId, decimal Rate, Instant EffectiveStart, Instant? EffectiveEnd);

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

/// <summary>One row of <see cref="CostQueryAssembly.LoadSubtreeAsync" />'s <c>job_node_subtrees</c> invocation.</summary>
internal sealed record SubtreeRow(long OriginRootId, long Id, long? ParentId, long? OwnerUserId, short? AchievementId);

/// <summary>One row of <see cref="CostQueryAssembly.ExtendAncestryAsync" />'s <c>job_node_ancestor_chains</c> invocation.</summary>
internal sealed record AncestorRow(long Id, long? ParentId, long? OwnerUserId);
