namespace JobTrack.Application;

using System.Diagnostics;
using Abstractions;
using Domain.Authorization;
using Domain.Costing;
using Domain.Hierarchy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Ports;

/// <summary>
///     Implements cost queries (plan §7.3 step 10) by loading each contributing worker's complete
///     immutable cost inputs through <see cref="ICostQueryPort" /> — including their database-wide
///     overlapping sessions under an internal elevated read scope for a correct concurrency divisor
///     (ADR 0017) — then running the pure <see cref="CostSegmentPartitioner" />/<see cref="CostEngine" />
///     pair once per worker and summing each worker's contribution. Every worker's sessions are costed
///     independently because rates, overrides, and concurrency are always resolved per worker (see
///     <see cref="Domain.Rates.RateResolver" />).
/// </summary>
internal sealed partial class CostQueries : ICostQueries, IRequesterDurationQueries
{
	private enum CostReadOperation
	{
		CostDetails,
		HierarchyTotals,
		BulkNodeCosts,
		RequesterVisibleHierarchy,
	}

	// Bounded ranges for cost responses (remediation plan §3.1): a cost trace/hierarchy is not
	// offset/limit-paginated like a flat collection -- reconciliation needs the whole subtree or
	// whole trace to produce a correct total, so truncating it would silently corrupt the numbers.
	// Instead, an oversized result is a hard validation failure (400) rather than a bounded array.
	// Set comfortably above the product's own documented and already-accepted scale target
	// (docs/plans/2026-07-09-overlapping-cost-scale-plan.md: 20,000 leaves total, with a single
	// heavy-worker branch legitimately reaching ~5,000 nodes/sessions) -- this is a backstop against
	// a pathological request, not a realistic product-scale ceiling.
	private const int MaxCostTraceSegments = 50_000;
	private const int MaxHierarchyNodeCount = 50_000;

	// A bulk request's candidate count is caller-controlled (one listing page's rows), so this is a
	// defensive backstop against a misbehaving caller, not a realistic page width -- the largest
	// paginated listing page this library serves is far smaller (fresh-eyes review §2.8). Exposed to
	// the assembly so JobQueries' listing enrichment can chunk a wider candidate set to this exact
	// bound rather than over-flow the port in a single call.
	internal const int MaxBulkNodeIdCount = 500;

	private readonly ICostQueryPort _port;
	private readonly ILogger<CostQueries> _logger;

	/// <summary>Creates a <see cref="CostQueries" /> over the given port with no growth-signal logging.</summary>
	public CostQueries(ICostQueryPort port) : this(port, null)
	{
	}

	/// <summary>
	///     Creates a <see cref="CostQueries" /> over the given port with a growth-signal logger (Stage 5b,
	///     large-database performance plan §4): one compact structured line per aggregate/leaf cost read
	///     recording DB-vs-engine duration split, cost-window span, contributing worker count, total
	///     session count and max/p50/p95 sessions per worker -- durations and counts only, never
	///     identities, rates or costs, so this is the Stage 3 trigger-decision input, not a diagnostic
	///     trace. <paramref name="logger" /> defaults to a no-op logger when <see langword="null" />.
	/// </summary>
	public CostQueries(ICostQueryPort port, ILogger<CostQueries>? logger)
	{
		ArgumentNullException.ThrowIfNull(port);

		_port = port;
		_logger = logger ?? NullLogger<CostQueries>.Instance;
	}

	/// <inheritdoc />
	public Task<CostDetailsResult> GetCostDetailsAsync(
		GetCostDetailsRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		var maxTraceSegments = ResolveBound(request.MaxTraceSegments, MaxCostTraceSegments, nameof(request.MaxTraceSegments));

		return GetCostDetailsCoreAsync(request, maxTraceSegments, cancellationToken);
	}

	/// <inheritdoc />
	public Task<HierarchyTotalsResult> GetHierarchyTotalsAsync(
		GetHierarchyTotalsRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		var maxHierarchyNodes = ResolveBound(request.MaxHierarchyNodes, MaxHierarchyNodeCount, nameof(request.MaxHierarchyNodes));

		return GetHierarchyTotalsCoreAsync(request, maxHierarchyNodes, cancellationToken);
	}

	/// <inheritdoc />
	public Task<BulkNodeCostResult> GetBulkNodeCostsAsync(GetBulkNodeCostsRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (request.NodeIds.Count > MaxBulkNodeIdCount) {
			throw new ArgumentOutOfRangeException(
				nameof(request), request.NodeIds.Count, $"A bulk cost request cannot price more than {MaxBulkNodeIdCount} node ids at once.");
		}

		return GetBulkNodeCostsCoreAsync(request, cancellationToken);
	}

	/// <inheritdoc />
	public async Task<EquatableDictionary<JobNodeId, AllocatedDuration>> GetRequesterVisibleHierarchyAsync(
		JobNodeId nodeId, Instant asOf, CancellationToken cancellationToken = default)
	{
		var dbStopwatch = Stopwatch.StartNew();
		var inputs = await _port.GetCostInputsAsync(nodeId, asOf, MaxHierarchyNodeCount, cancellationToken).ConfigureAwait(false);
		dbStopwatch.Stop();
		var engineStopwatch = Stopwatch.StartNew();
		var allocatedDurations = new Dictionary<JobNodeId, AllocatedDuration>(
			HierarchicalAllocatedDurationAggregator.Aggregate(
				nodeId,
				inputs.NodesById,
				new Dictionary<JobNodeId, AllocatedDuration>()));

		foreach (var worker in inputs.Workers) {
			cancellationToken.ThrowIfCancellationRequested();
			MergeDurations(allocatedDurations, ComputeWorkerDurationContribution(worker, nodeId, inputs));
		}

		engineStopwatch.Stop();
		LogGrowthSignal(CostReadOperation.RequesterVisibleHierarchy, inputs, dbStopwatch.Elapsed, engineStopwatch.Elapsed);

		return EquatableDictionaryFactory.CopyOf(allocatedDurations);
	}

	/// <summary>One worker's duration-only contribution (no rates, no trace) -- self-contained, so safe to run on any thread (Stage 1).</summary>
	private static IReadOnlyDictionary<JobNodeId, AllocatedDuration> ComputeWorkerDurationContribution(
		WorkerCostInputs worker, JobNodeId nodeId, CostQueryResult inputs)
	{
		var allocations = CostSegmentPartitioner.Partition(
			worker.Sessions,
			worker.EffectiveWorkingIntervals,
			inputs.NodesById,
			[],
			[],
			inputs.Bounds);
		var leafDurations = AllocatedDurationCalculator.ComputeLeafDurations(allocations);
		return HierarchicalAllocatedDurationAggregator.Aggregate(nodeId, inputs.NodesById, leafDurations);
	}

	private async Task<(
		CostQueryResult Inputs,
		Dictionary<JobNodeId, Money> ExactCosts,
		Dictionary<JobNodeId, AllocatedDuration> AllocatedDurations,
		List<CostSegmentTrace> Trace)> CalculateAsync(
		AppUserId actorId, JobNodeId nodeId, Instant asOf, int maxTraceSegments, CancellationToken cancellationToken) =>
		await CalculateAsync(
			actorId, nodeId, asOf, MaxHierarchyNodeCount, maxTraceSegments, CostReadOperation.CostDetails, cancellationToken).ConfigureAwait(false);

	private async Task<(
		CostQueryResult Inputs,
		Dictionary<JobNodeId, Money> ExactCosts,
		Dictionary<JobNodeId, AllocatedDuration> AllocatedDurations,
		List<CostSegmentTrace> Trace)> CalculateAsync(
		AppUserId actorId, JobNodeId nodeId, Instant asOf, int maxHierarchyNodes, int? maxTraceSegments, CostReadOperation operation,
		CancellationToken cancellationToken)
	{
		var dbStopwatch = Stopwatch.StartNew();
		var access = await _port.GetCostAccessInputsAsync(actorId, nodeId, cancellationToken).ConfigureAwait(false);
		if (!CostAccessPolicy.CanView(access.ActorRoles, access.AncestorOwnerIds.Contains(actorId))) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not view costs for node {nodeId}.");
		}

		var inputs = await _port.GetCostInputsAsync(nodeId, asOf, maxHierarchyNodes, cancellationToken).ConfigureAwait(false);
		dbStopwatch.Stop();
		var engineStopwatch = Stopwatch.StartNew();

		var exactCosts = new Dictionary<JobNodeId, Money>();
		var allocatedDurations = new Dictionary<JobNodeId, AllocatedDuration>();
		var trace = new List<CostSegmentTrace>();
		var includedNodeIds = GetSubtreeNodeIds(nodeId, inputs.NodesById);

		foreach (var worker in inputs.Workers) {
			cancellationToken.ThrowIfCancellationRequested();
			var remainingTraceSegments = maxTraceSegments.HasValue ? maxTraceSegments.Value - trace.Count : int.MaxValue;

			IReadOnlyDictionary<JobNodeId, Money> workerExactCosts;
			IReadOnlyDictionary<JobNodeId, AllocatedDuration> workerAllocatedDurations;
			if (maxTraceSegments.HasValue) {
				var allocations = CostSegmentPartitioner.PartitionBounded(
					worker.Sessions, worker.EffectiveWorkingIntervals, inputs.NodesById,
					worker.Exceptions, worker.NodeOverrides, worker.UserCostRates, inputs.Bounds,
					includedNodeIds, remainingTraceSegments);
				var calculation = CostEngine.Calculate(
					nodeId, allocations, inputs.NodesById, worker.ScheduledWorkingIntervals, worker.Exceptions, worker.NodeOverrides,
					worker.UserCostRates, worker.UserDefaultRate);
				workerExactCosts = calculation.ExactCosts;
				workerAllocatedDurations = calculation.AllocatedDurations;
				trace.AddRange(calculation.Trace);
			} else {
				(workerExactCosts, workerAllocatedDurations) = ComputeWorkerAggregateContribution(worker, nodeId, inputs, includedNodeIds);
			}

			MergeCosts(exactCosts, workerExactCosts);
			MergeDurations(allocatedDurations, workerAllocatedDurations);
		}

		engineStopwatch.Stop();
		LogGrowthSignal(operation, inputs, dbStopwatch.Elapsed, engineStopwatch.Elapsed);

		return (inputs, exactCosts, allocatedDurations, trace);
	}

	/// <summary>
	///     Stage 5b's compact growth-signal line (large-database performance plan §4): the Stage 3
	///     trigger-decision input, so it must stay cheap to compute and small to store. Session-count
	///     percentiles use a full sort because the realistic worker-per-request count (tens, not
	///     thousands -- ADR 0017 scopes this to one subtree's contributors) makes that the simplest
	///     correct choice, not a hot-path concern.
	/// </summary>
	private void LogGrowthSignal(
		CostReadOperation operation, CostQueryResult inputs, TimeSpan dbDuration, TimeSpan engineDuration) =>
		LogGrowthSignal(operation, inputs.Workers, inputs.Bounds, dbDuration, engineDuration);

	private void LogGrowthSignal(
		CostReadOperation operation, BulkCostQueryResult inputs, TimeSpan dbDuration, TimeSpan engineDuration) =>
		LogGrowthSignal(operation, inputs.Workers, inputs.Bounds, dbDuration, engineDuration);

	private void LogGrowthSignal(
		CostReadOperation operation, EquatableArray<WorkerCostInputs> workers, Domain.Intervals.WorkInterval bounds,
		TimeSpan dbDuration, TimeSpan engineDuration)
	{
		if (!_logger.IsEnabled(LogLevel.Information)) {
			return;
		}

		var workerCount = workers.Count;
		var sessionCounts = new int[workerCount];
		var totalSessions = 0;
		for (var index = 0; index < workerCount; ++index) {
			var count = workers[index].Sessions.Count;
			sessionCounts[index] = count;
			totalSessions += count;
		}

		Array.Sort(sessionCounts);
		var maxSessions = workerCount == 0 ? 0 : sessionCounts[^1];
		var p50Sessions = workerCount == 0 ? 0 : sessionCounts[(workerCount - 1) / 2];
		var p95Sessions = workerCount == 0 ? 0 : sessionCounts[(int)((workerCount - 1) * 0.95)];
		var windowTicks = bounds.Duration.BclCompatibleTicks;

		LogCostReadGrowthSignal(
			_logger, operation, (long)dbDuration.TotalMilliseconds, (long)engineDuration.TotalMilliseconds,
			windowTicks, workerCount, totalSessions, maxSessions, p50Sessions, p95Sessions);
	}

	[LoggerMessage(
		Level = LogLevel.Information,
		Message =
			"cost_read_growth_signal operation={Operation} db_ms={DbMs} engine_ms={EngineMs} window_ticks={WindowTicks} workers={Workers} " +
			"sessions_total={SessionsTotal} sessions_max={SessionsMax} sessions_p50={SessionsP50} sessions_p95={SessionsP95}")]
	private static partial void LogCostReadGrowthSignal(
		ILogger logger, CostReadOperation operation, long dbMs, long engineMs, long windowTicks, int workers, int sessionsTotal, int sessionsMax, int sessionsP50,
		int sessionsP95);

	/// <summary>One worker's aggregate-only (no trace) cost and duration contribution -- self-contained, so safe to run on any thread (Stage 1).</summary>
	private static (IReadOnlyDictionary<JobNodeId, Money> ExactCosts, IReadOnlyDictionary<JobNodeId, AllocatedDuration> AllocatedDurations)
		ComputeWorkerAggregateContribution(
			WorkerCostInputs worker, JobNodeId nodeId, CostQueryResult inputs, HashSet<JobNodeId> includedNodeIds)
	{
		var allocations = CostSegmentPartitioner.PartitionBounded(
			worker.Sessions, worker.EffectiveWorkingIntervals, inputs.NodesById,
			worker.Exceptions, worker.NodeOverrides, worker.UserCostRates, inputs.Bounds,
			includedNodeIds, int.MaxValue);
		var leafCosts = CostEngine.ComputeLeafCosts(
			allocations, inputs.NodesById, worker.Exceptions, worker.NodeOverrides, worker.UserCostRates, worker.UserDefaultRate);
		var workerExactCosts = HierarchicalCostAggregator.Aggregate(nodeId, inputs.NodesById, leafCosts);
		var leafDurations = AllocatedDurationCalculator.ComputeLeafDurations(allocations);
		var workerAllocatedDurations = HierarchicalAllocatedDurationAggregator.Aggregate(nodeId, inputs.NodesById, leafDurations);
		return (workerExactCosts, workerAllocatedDurations);
	}

	private static void MergeCosts(Dictionary<JobNodeId, Money> target, IReadOnlyDictionary<JobNodeId, Money> source)
	{
		foreach (var (id, amount) in source) {
			target[id] = new(target.GetValueOrDefault(id, new(0m)).Amount + amount.Amount);
		}
	}

	private static void MergeDurations(Dictionary<JobNodeId, AllocatedDuration> target, IReadOnlyDictionary<JobNodeId, AllocatedDuration> source)
	{
		foreach (var (id, duration) in source) {
			target[id] = target.GetValueOrDefault(id, AllocatedDuration.Zero).Add(duration);
		}
	}

	private static HashSet<JobNodeId> GetSubtreeNodeIds(
		JobNodeId rootId, EquatableDictionary<JobNodeId, HierarchyNode> nodesById)
	{
		var result = new HashSet<JobNodeId>();
		var pending = new Stack<JobNodeId>();
		pending.Push(rootId);
		while (pending.Count > 0) {
			var id = pending.Pop();
			if (!result.Add(id)) {
				continue;
			}

			foreach (var childId in nodesById[id].ChildIds) {
				pending.Push(childId);
			}
		}

		return result;
	}

	private static int ResolveBound(int? requested, int maximum, string parameterName)
	{
		if (requested is null) {
			return maximum;
		}

		if (requested <= 0) {
			throw new ArgumentOutOfRangeException(parameterName, requested, "The maximum result size must be positive.");
		}

		if (requested > maximum) {
			throw new ArgumentOutOfRangeException(parameterName, requested, $"The maximum result size cannot exceed {maximum}.");
		}

		return requested.Value;
	}

	private static Dictionary<JobNodeId, Money> ReconcileHierarchy(
		JobNodeId nodeId, EquatableDictionary<JobNodeId, HierarchyNode> nodesById, Dictionary<JobNodeId, Money> exactCosts)
	{
		var rootExact = exactCosts.GetValueOrDefault(nodeId, new(0m));
		var displayed = new Dictionary<JobNodeId, Money> { [nodeId] = rootExact.RoundToPennies() };

		var pending = new Queue<JobNodeId>();
		pending.Enqueue(nodeId);
		while (pending.Count > 0) {
			var id = pending.Dequeue();
			var node = nodesById[id];
			if (node.ChildIds.Count == 0) {
				continue;
			}

			var children = node.ChildIds
				.Select(childId => (ChildId: childId, ExactAmount: exactCosts.GetValueOrDefault(childId, new(0m))))
				.ToList();
			var reconciled = HierarchyDisplayReconciler.Reconcile(exactCosts.GetValueOrDefault(id, new(0m)), children);

			foreach (var child in reconciled) {
				displayed[child.ChildId] = child.DisplayedAmount;
				pending.Enqueue(child.ChildId);
			}
		}

		return displayed;
	}

	private Task<CostDetailsResult> GetCostDetailsCoreAsync(
		GetCostDetailsRequest request, int maxTraceSegments, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"costs.get-details", request.Context, JobTrackOperation.WithNodeId(request.NodeId),
			async () => {
				var (_, exactCosts, allocatedDurations, trace) = await CalculateAsync(
						request.Context.Actor, request.NodeId, request.AsOf, maxTraceSegments, cancellationToken)
					.ConfigureAwait(false);

				if (trace.Count > maxTraceSegments) {
					throw new ArgumentOutOfRangeException(
						nameof(request),
						trace.Count,
						$"This node's cost trace has {trace.Count} segments, exceeding the {maxTraceSegments}-segment maximum. Narrow the query (e.g. a leaf rather than a large subtree).");
				}

				var exact = exactCosts.GetValueOrDefault(request.NodeId, new(0m));

				return new CostDetailsResult {
					NodeId = request.NodeId,
					ExactCost = exact,
					DisplayedCost = exact.RoundToPennies(),
					AllocatedDuration = allocatedDurations.GetValueOrDefault(request.NodeId, AllocatedDuration.Zero),
					Trace = EquatableArray.CopyOf(trace.OrderBy(entry => entry.Segment.Start).ThenBy(entry => entry.SessionId.Value)),
					TzdbVersion = DateTimeZoneProviders.Tzdb.VersionId,
				};
			});

	private Task<HierarchyTotalsResult> GetHierarchyTotalsCoreAsync(
		GetHierarchyTotalsRequest request, int maxHierarchyNodes, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"costs.get-hierarchy-totals", request.Context, JobTrackOperation.WithNodeId(request.NodeId),
			async () => {
				var (inputs, exactCosts, allocatedDurations, _) = await CalculateAsync(
						request.Context.Actor, request.NodeId, request.AsOf, maxHierarchyNodes, null,
						CostReadOperation.HierarchyTotals, cancellationToken)
				.ConfigureAwait(false);

				var displayedCosts = ReconcileHierarchy(request.NodeId, inputs.NodesById, exactCosts);
				var completeAllocatedDurations = displayedCosts.Keys.ToDictionary(
					id => id,
					id => allocatedDurations.GetValueOrDefault(id, AllocatedDuration.Zero));

				if (displayedCosts.Count > maxHierarchyNodes) {
					throw new ArgumentOutOfRangeException(
						nameof(request),
						displayedCosts.Count,
						$"This node's subtree has {displayedCosts.Count} nodes, exceeding the {maxHierarchyNodes}-node maximum. Query a smaller subtree.");
				}

				return new HierarchyTotalsResult {
					NodeId = request.NodeId,
					ExactCosts = EquatableDictionaryFactory.CopyOf(exactCosts),
					DisplayedCosts = EquatableDictionaryFactory.CopyOf(displayedCosts),
					AllocatedDurations = EquatableDictionaryFactory.CopyOf(completeAllocatedDurations),
					TzdbVersion = DateTimeZoneProviders.Tzdb.VersionId,
				};
			});

	private Task<BulkNodeCostResult> GetBulkNodeCostsCoreAsync(GetBulkNodeCostsRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"costs.get-bulk-node-costs", request.Context, null,
			async () => {
				if (request.NodeIds.Count == 0) {
					return new() {
						DisplayedCosts = EquatableDictionaryFactory.CopyOf(new Dictionary<JobNodeId, Money>()),
						AllocatedDurations = EquatableDictionaryFactory.CopyOf(new Dictionary<JobNodeId, AllocatedDuration>()),
					};
				}

				var dbStopwatch = Stopwatch.StartNew();
				var inputs = await _port.GetBulkCostInputsAsync(
						request.Context.Actor, request.NodeIds, request.AsOf, MaxHierarchyNodeCount, cancellationToken)
					.ConfigureAwait(false);
				dbStopwatch.Stop();
				var engineStopwatch = Stopwatch.StartNew();

				// ADR 0040: a candidate is only costable if the actor is admin/cost-viewer or owns the
				// node or one of its ancestors -- walked from the one already-materialized snapshot, so
				// this adds no further round trips no matter how many candidates the page holds.
				var authorizedNodeIds = request.NodeIds
					.Where(nodeId => inputs.NodesById.ContainsKey(nodeId)
									 && CostAccessPolicy.CanView(
										 inputs.ActorRoles,
										 OwnsNodeOrAncestor(nodeId, request.Context.Actor, inputs.NodesById, inputs.OwnerUserIdsById)))
					.ToArray();

				// Every worker's leaf costs accumulate into one combined map first, so the hierarchy is
				// walked once for the whole page rather than once per (worker, candidate) pair. Summing
				// leaf costs before aggregating is exact: a root's total is a plain sum over the costed
				// leaves beneath it, so summing per worker and then per root, or per root over the
				// already-combined leaves, adds the same set of decimal amounts either way.
				var combinedLeafCosts = new Dictionary<JobNodeId, decimal>();
				var combinedLeafDurations = new Dictionary<JobNodeId, AllocatedDuration>();
				foreach (var worker in inputs.Workers) {
					cancellationToken.ThrowIfCancellationRequested();
					var (costs, durations) = ComputeWorkerLeafContribution(worker, inputs);
					MergeLeafContributions(combinedLeafCosts, combinedLeafDurations, costs, durations);
				}

				var exactCosts = HierarchicalCostAggregator.SumSubtreeTotals(
					authorizedNodeIds,
					inputs.NodesById,
					combinedLeafCosts.ToDictionary(entry => entry.Key, entry => new Money(entry.Value)));

				var displayedCosts = authorizedNodeIds.ToDictionary(
					nodeId => nodeId, nodeId => exactCosts.GetValueOrDefault(nodeId, new(0m)).RoundToPennies());
				var allocatedDurations = HierarchicalAllocatedDurationAggregator.SumSubtreeTotals(
					authorizedNodeIds, inputs.NodesById, combinedLeafDurations);

				engineStopwatch.Stop();
				LogGrowthSignal(CostReadOperation.BulkNodeCosts, inputs, dbStopwatch.Elapsed, engineStopwatch.Elapsed);

				return new BulkNodeCostResult {
					DisplayedCosts = EquatableDictionaryFactory.CopyOf(displayedCosts),
					AllocatedDurations = EquatableDictionaryFactory.CopyOf(allocatedDurations),
				};
			});

	private static (IReadOnlyDictionary<JobNodeId, Money> Costs, IReadOnlyDictionary<JobNodeId, AllocatedDuration> Durations)
		ComputeWorkerLeafContribution(WorkerCostInputs worker, BulkCostQueryResult inputs)
	{
		var allocations = CostSegmentPartitioner.Partition(
			worker.Sessions, worker.EffectiveWorkingIntervals, inputs.NodesById,
			worker.Exceptions, worker.NodeOverrides, worker.UserCostRates, inputs.Bounds);
		var costs = CostEngine.ComputeLeafCosts(
			allocations, inputs.NodesById, worker.Exceptions, worker.NodeOverrides, worker.UserCostRates, worker.UserDefaultRate);
		var durations = AllocatedDurationCalculator.ComputeLeafDurations(allocations);
		return (costs, durations);
	}

	private static void MergeLeafContributions(
		Dictionary<JobNodeId, decimal> targetCosts,
		Dictionary<JobNodeId, AllocatedDuration> targetDurations,
		IReadOnlyDictionary<JobNodeId, Money> costs,
		IReadOnlyDictionary<JobNodeId, AllocatedDuration> durations)
	{
		foreach (var (leafId, amount) in costs) {
			targetCosts[leafId] = targetCosts.GetValueOrDefault(leafId) + amount.Amount;
		}

		foreach (var (leafId, duration) in durations) {
			targetDurations[leafId] = targetDurations.GetValueOrDefault(leafId, AllocatedDuration.Zero).Add(duration);
		}
	}

	/// <summary>Whether <paramref name="actorId" /> owns <paramref name="nodeId" /> or any of its ancestors, walked entirely in memory (ADR 0040).</summary>
	private static bool OwnsNodeOrAncestor(
		JobNodeId nodeId, AppUserId actorId,
		EquatableDictionary<JobNodeId, HierarchyNode> nodesById, EquatableDictionary<JobNodeId, AppUserId?> ownersById)
	{
		JobNodeId? current = nodeId;
		while (current is JobNodeId currentId) {
			if (ownersById.GetValueOrDefault(currentId) == actorId) {
				return true;
			}

			current = nodesById[currentId].ParentId;
		}

		return false;
	}
}
