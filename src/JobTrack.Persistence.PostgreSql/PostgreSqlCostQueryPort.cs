namespace JobTrack.Persistence.PostgreSql;

using Abstractions;
using Application;
using Application.Ports;
using Domain.Costing;
using Domain.Hierarchy;
using Domain.Intervals;
using Domain.Rates;
using Domain.Schedules;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Shared;
using Shared.Entities;

/// <summary>
///     PostgreSQL implementation of <see cref="ICostQueryPort" /> (impl plan §7.3/§7.4 slice 10:
///     calculate cost details and hierarchy totals). One <see cref="PostgreSqlJobTrackDbContext" /> per
///     call, read-only throughout. Materializes the whole <c>job_node</c> tree and every worker's
///     database-wide sessions, schedules, exceptions, overrides, and rates -- the internal elevated
///     read scope ADR 0017 requires for a correct concurrency divisor -- leaving every authorization
///     decision and the actual cost calculation to <see cref="CostQueries" /> and the pure domain engine.
///     Schedule expansion (<see cref="ScheduleExpander" />) and exception resolution (
///     <see
///         cref="ScheduleExceptionResolver" />
///     ) are explicitly domain, not schema-layer, concerns (schema
///     version 0015's header), so this port calls them itself over the raw historical schedule rows.
/// </summary>
internal sealed class PostgreSqlCostQueryPort : ICostQueryPort
{
	private readonly NpgsqlDataSource dataSource;

	/// <summary>Creates the port over the given pooled <see cref="NpgsqlDataSource" />.</summary>
	public PostgreSqlCostQueryPort(NpgsqlDataSource dataSource) => this.dataSource = dataSource;

	/// <inheritdoc />
	public async Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		AppUserId actorId, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		return await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task<CostQueryResult> GetCostInputsAsync(
		AppUserId actorId, JobNodeId nodeId, Instant asOf, int maxHierarchyNodes, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await PostgreSqlCostQuerySnapshot.BeginAsync(context, cancellationToken).ConfigureAwait(false);

		var actorRoles = await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);

		var nodesById = await CostQueryAssembly.LoadNodesByIdAsync(context, cancellationToken).ConfigureAwait(false);
		if (!nodesById.ContainsKey(nodeId)) {
			throw new EntityNotFoundException($"Job node {nodeId} does not exist.");
		}

		var subtreeNodeCount = CostQueryAssembly.CountSubtreeNodes(nodeId, nodesById);
		if (subtreeNodeCount > maxHierarchyNodes) {
			throw new ArgumentOutOfRangeException(
				nameof(maxHierarchyNodes),
				subtreeNodeCount,
				$"This node's subtree has {subtreeNodeCount} nodes, exceeding the {maxHierarchyNodes}-node maximum. Query a smaller subtree.");
		}

		var (bounds, workers) = await CostQueryAssembly.LoadWorkersAsync(
			context, nodeId, nodesById, asOf, cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		return new() {
			ActorRoles = actorRoles,
			NodesById = EquatableDictionaryFactory.CopyOf(nodesById),
			Bounds = bounds,
			Workers = EquatableArray.CopyOf(workers),
		};
	}

	/// <inheritdoc />
	public async Task<EquatableArray<AppUserId>> GetAncestorOwnerIdsAsync(
		JobNodeId nodeId, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		var ownerIds = await JobNodeHierarchyQueries.GetAncestorOwnerIdsAsync(context, nodeId.Value, cancellationToken).ConfigureAwait(false);
		return EquatableArray.CopyOf(ownerIds.Select(id => new AppUserId(id)));
	}

	private PostgreSqlJobTrackDbContext CreateContext()
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, o => o.UseNodaTime())
			.Options;

		return new(options);
	}

	private static async Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		PostgreSqlJobTrackDbContext context, AppUserId actorId, CancellationToken cancellationToken)
	{
		var actorIdentityUser = await context.Set<IdentityUserEntity>().AsNoTracking()
									.FirstOrDefaultAsync(iu => iu.AppUserId == actorId, cancellationToken).ConfigureAwait(false)
								?? throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		ActorAccountState.EnsureMayAct(actorIdentityUser, actorId, SystemClock.Instance.GetCurrentInstant());

		var roles = await context.Set<IdentityUserRoleEntity>().AsNoTracking()
			.Where(ur => ur.IdentityUserId == actorIdentityUser.Id)
			.Select(ur => (EmployeeRole)ur.IdentityRoleId)
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);

		return [.. roles];
	}
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
	public static async Task<Dictionary<JobNodeId, HierarchyNode>> LoadNodesByIdAsync(DbContext context, CancellationToken cancellationToken)
	{
		var nodes = await context.Set<JobNodeEntity>().AsNoTracking()
			.Select(n => new { n.Id, n.ParentId })
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		var achievements = await context.Set<LeafWorkEntity>().AsNoTracking()
			.ToDictionaryAsync(lw => lw.JobNodeId, lw => lw.Achievement, cancellationToken).ConfigureAwait(false);

		var childrenByParent = nodes
			.Where(n => n.ParentId is not null)
			.GroupBy(n => n.ParentId!.Value)
			.ToDictionary(group => group.Key, group => EquatableArray.CopyOf(group.Select(n => n.Id)));

		return nodes.ToDictionary(
			n => n.Id,
			n => new HierarchyNode(
				n.Id,
				n.ParentId,
				childrenByParent.TryGetValue(n.Id, out var children) ? children : [],
				achievements.TryGetValue(n.Id, out var achievement) ? achievement : null));
	}

	public static async Task<(WorkInterval Bounds, List<WorkerCostInputs> Workers)> LoadWorkersAsync(
		DbContext context, JobNodeId nodeId, IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById,
		Instant asOf, CancellationToken cancellationToken)
	{
		var requestedNodeIds = GetSubtreeIds(nodeId, nodesById);
		var requestedSessionStarts = await context.Set<WorkSessionEntity>().AsNoTracking()
			.Where(s => requestedNodeIds.Contains(s.LeafWorkId) && s.StartedAt < asOf)
			.Select(s => s.StartedAt)
			.ToListAsync(cancellationToken).ConfigureAwait(false);
		if (requestedSessionStarts.Count == 0) {
			return (new(Instant.MinValue, asOf), []);
		}

		var bounds = new WorkInterval(requestedSessionStarts.Min(), asOf);
		var workerIds = await context.Set<WorkSessionEntity>().AsNoTracking()
			.Where(s => requestedNodeIds.Contains(s.LeafWorkId) && s.StartedAt < asOf)
			.Select(s => s.WorkedByUserId)
			.Distinct()
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		var scheduleVersions = await context.Set<ScheduleVersionEntity>().AsNoTracking()
			.Where(v => workerIds.Contains(v.UserId)).ToListAsync(cancellationToken).ConfigureAwait(false);
		var scheduleVersionIds = scheduleVersions.Select(v => v.Id).ToList();
		var scheduleIntervals = await context.Set<ScheduleIntervalEntity>().AsNoTracking()
			.Where(i => scheduleVersionIds.Contains(i.ScheduleVersionId)).ToListAsync(cancellationToken).ConfigureAwait(false);
		var exceptions = await context.Set<ScheduleExceptionEntity>().AsNoTracking()
			.Where(e => workerIds.Contains(e.UserId) && e.StartedAt < bounds.End && e.FinishedAt > bounds.Start)
			.ToListAsync(cancellationToken).ConfigureAwait(false);
		var nodeOverrides = await context.Set<NodeRateOverrideEntity>().AsNoTracking()
			.Where(o => workerIds.Contains(o.UserId) && o.EffectiveStart < bounds.End
													 && (o.EffectiveEnd == null || o.EffectiveEnd > bounds.Start))
			.ToListAsync(cancellationToken).ConfigureAwait(false);
		var userCostRates = await context.Set<UserCostRateEntity>().AsNoTracking()
			.Where(r => workerIds.Contains(r.UserId) && r.EffectiveStart < bounds.End
													 && (r.EffectiveEnd == null || r.EffectiveEnd > bounds.Start))
			.ToListAsync(cancellationToken).ConfigureAwait(false);
		var appUsersById = await context.Set<AppUserEntity>().AsNoTracking()
			.Where(u => workerIds.Contains(u.Id))
			.ToDictionaryAsync(u => u.Id, cancellationToken).ConfigureAwait(false);

		var intervalsByVersion = scheduleIntervals.GroupBy(i => i.ScheduleVersionId).ToDictionary(group => group.Key, group => group.ToList());

		var workers = new List<WorkerCostInputs>();
		foreach (var workerId in workerIds) {
			var workerSessions = await LoadWorkerSessionsAsync(context, workerId, bounds, asOf, cancellationToken).ConfigureAwait(false);

			var expandedScheduleIntervals = new List<WorkInterval>();
			foreach (var version in scheduleVersions.Where(v => v.UserId == workerId)) {
				var weeklyIntervals = intervalsByVersion.GetValueOrDefault(version.Id, [])
					.Select(i => new WeeklyInterval(i.DayOfWeek, i.StartTime, i.EndTime));
				var scheduleVersion = new ScheduleVersion(
					StoredTimeZoneResolver.Resolve(version.IanaTimeZone, $"Schedule version {version.Id}"),
					version.EffectiveStart, version.EffectiveEnd,
					EquatableArray.CopyOf(weeklyIntervals));
				expandedScheduleIntervals.AddRange(ScheduleExpander.Expand(scheduleVersion, bounds));
			}

			var workerExceptions = exceptions
				.Where(e => e.UserId == workerId)
				.Select(e => new ScheduleExceptionEntry(
					(ScheduleExceptionEffect)e.ScheduleExceptionEffectId, new(e.StartedAt, e.FinishedAt), e.RateOverride))
				.ToArray();

			var normalizedScheduled = IntervalAlgebra.Normalize(expandedScheduleIntervals);
			var effectiveWorkingIntervals = ScheduleExceptionResolver.Apply(expandedScheduleIntervals, workerExceptions);

			var workerNodeOverrides = nodeOverrides
				.Where(o => o.UserId == workerId)
				.Select(o => new NodeRateOverride(o.NodeId, o.Rate, o.EffectiveStart, o.EffectiveEnd))
				.ToArray();

			var workerUserCostRates = userCostRates
				.Where(r => r.UserId == workerId)
				.Select(r => new UserCostRate(r.Rate, r.EffectiveStart, r.EffectiveEnd))
				.ToArray();

			workers.Add(new() {
				Sessions = EquatableArray.CopyOf(workerSessions),
				EffectiveWorkingIntervals = EquatableArray.CopyOf(effectiveWorkingIntervals),
				ScheduledWorkingIntervals = EquatableArray.CopyOf(normalizedScheduled),
				Exceptions = EquatableArray.CopyOf(workerExceptions),
				NodeOverrides = EquatableArray.CopyOf(workerNodeOverrides),
				UserCostRates = EquatableArray.CopyOf(workerUserCostRates),
				UserDefaultRate = appUsersById.TryGetValue(workerId, out var appUser) ? appUser.DefaultHourlyRate : null,
			});
		}

		return (bounds, workers);
	}

	public static int CountSubtreeNodes(JobNodeId nodeId, IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById) =>
		GetSubtreeIds(nodeId, nodesById).Count;

	private static List<JobNodeId> GetSubtreeIds(
		JobNodeId nodeId, IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById)
	{
		var result = new List<JobNodeId>();
		var pending = new Stack<JobNodeId>();
		pending.Push(nodeId);
		while (pending.TryPop(out var current)) {
			result.Add(current);
			foreach (var childId in nodesById[current].ChildIds) {
				pending.Push(childId);
			}
		}

		return result;
	}

	private static Instant ClipEnd(Instant? finishedAt, Instant asOf) =>
		finishedAt is { } finish && finish < asOf ? finish : asOf;

	/// <summary>
	///     Loads one worker's database-wide overlapping sessions (ADR 0017's elevated read scope)
	///     through the <c>worker_overlapping_sessions</c> stored function (schema version 0018) rather
	///     than duplicating its predicate in LINQ -- ADR 0010 names both "database-wide overlap
	///     discovery" and "the canonical cost-input queries" as the sanctioned reason this function
	///     exists, and only a query expressed against the generated <c>session_range</c> column lets the
	///     planner use <c>work_session_user_range_gist_idx</c> instead of filtering the worker's entire
	///     history in memory. <see cref="OverlappingSessionRow.FinishedAt" /> is deliberately not clipped
	///     to <paramref name="asOf" /> by the function itself (its own <c>effective_finished_at</c> column
	///     is not selected here) -- <see cref="ClipEnd" /> does that, exactly as before this change.
	/// </summary>
	private static async Task<CostableSession[]> LoadWorkerSessionsAsync(
		DbContext context, AppUserId workerId, WorkInterval bounds, Instant asOf, CancellationToken cancellationToken)
	{
		var rows = await context.Database.SqlQuery<OverlappingSessionRow>(
			$"""
			 SELECT session_id AS "SessionId", leaf_work_id AS "LeafWorkId", started_at AS "StartedAt", finished_at AS "FinishedAt"
			 FROM worker_overlapping_sessions({workerId.Value}, {bounds.Start}, {bounds.End}, {asOf})
			 """).ToListAsync(cancellationToken).ConfigureAwait(false);

		return rows
			.Select(row => new CostableSession(
				new(row.SessionId), new(row.LeafWorkId), new(row.StartedAt, ClipEnd(row.FinishedAt, asOf))))
			.ToArray();
	}
}

/// <summary>One row of <see cref="CostQueryAssembly.LoadWorkerSessionsAsync" />, mapping <c>worker_overlapping_sessions</c>'s output columns.</summary>
internal sealed record OverlappingSessionRow(long SessionId, long LeafWorkId, Instant StartedAt, Instant? FinishedAt);
