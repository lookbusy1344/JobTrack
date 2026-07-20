namespace JobTrack.Application;

using Abstractions;
using Domain.Authorization;
using Domain.Costing;
using Domain.Hierarchy;
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
public sealed class CostQueries : ICostQueries
{
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
	// paginated listing page this library serves is far smaller (fresh-eyes review §2.8).
	private const int MaxBulkNodeIdCount = 500;

	private readonly ICostQueryPort _port;

	/// <summary>Creates a <see cref="CostQueries" /> over the given port.</summary>
	public CostQueries(ICostQueryPort port)
	{
		ArgumentNullException.ThrowIfNull(port);

		_port = port;
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

	private async Task<(CostQueryResult Inputs, Dictionary<JobNodeId, Money> ExactCosts, List<CostSegmentTrace> Trace)> CalculateAsync(
		AppUserId actorId, JobNodeId nodeId, Instant asOf, CancellationToken cancellationToken) =>
		await CalculateAsync(actorId, nodeId, asOf, MaxHierarchyNodeCount, cancellationToken).ConfigureAwait(false);

	private async Task<(CostQueryResult Inputs, Dictionary<JobNodeId, Money> ExactCosts, List<CostSegmentTrace> Trace)> CalculateAsync(
		AppUserId actorId, JobNodeId nodeId, Instant asOf, int maxHierarchyNodes, CancellationToken cancellationToken)
	{
		var actorRoles = await _port.GetActorRolesAsync(actorId, cancellationToken).ConfigureAwait(false);
		var ancestorOwnerIds = await _port.GetAncestorOwnerIdsAsync(nodeId, cancellationToken).ConfigureAwait(false);
		if (!CostAccessPolicy.CanView(actorRoles, ancestorOwnerIds.Contains(actorId))) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not view costs for node {nodeId}.");
		}

		var inputs = await _port.GetCostInputsAsync(actorId, nodeId, asOf, maxHierarchyNodes, cancellationToken).ConfigureAwait(false);

		var exactCosts = new Dictionary<JobNodeId, Money>();
		var trace = new List<CostSegmentTrace>();
		foreach (var worker in inputs.Workers) {
			var allocations = CostSegmentPartitioner.Partition(
				worker.Sessions, worker.EffectiveWorkingIntervals, inputs.NodesById,
				worker.Exceptions, worker.NodeOverrides, worker.UserCostRates, inputs.Bounds);
			var calculation = CostEngine.Calculate(
				nodeId, allocations, inputs.NodesById, worker.ScheduledWorkingIntervals, worker.Exceptions, worker.NodeOverrides,
				worker.UserCostRates, worker.UserDefaultRate);

			foreach (var (id, amount) in calculation.ExactCosts) {
				exactCosts[id] = new(exactCosts.GetValueOrDefault(id, new(0m)).Amount + amount.Amount);
			}

			trace.AddRange(calculation.Trace);
		}

		return (inputs, exactCosts, trace);
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
				var (_, exactCosts, trace) = await CalculateAsync(request.Context.Actor, request.NodeId, request.AsOf, cancellationToken)
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
					Trace = EquatableArray.CopyOf(trace.OrderBy(entry => entry.Segment.Start).ThenBy(entry => entry.SessionId.Value)),
					TzdbVersion = DateTimeZoneProviders.Tzdb.VersionId,
				};
			});

	private Task<HierarchyTotalsResult> GetHierarchyTotalsCoreAsync(
		GetHierarchyTotalsRequest request, int maxHierarchyNodes, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"costs.get-hierarchy-totals", request.Context, JobTrackOperation.WithNodeId(request.NodeId),
			async () => {
				var (inputs, exactCosts, _) = await CalculateAsync(
						request.Context.Actor, request.NodeId, request.AsOf, maxHierarchyNodes, cancellationToken)
					.ConfigureAwait(false);

				var displayedCosts = ReconcileHierarchy(request.NodeId, inputs.NodesById, exactCosts);

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
					TzdbVersion = DateTimeZoneProviders.Tzdb.VersionId,
				};
			});

	private Task<BulkNodeCostResult> GetBulkNodeCostsCoreAsync(GetBulkNodeCostsRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"costs.get-bulk-node-costs", request.Context, null,
			async () => {
				if (request.NodeIds.Count == 0) {
					return new() { DisplayedCosts = EquatableDictionaryFactory.CopyOf(new Dictionary<JobNodeId, Money>()) };
				}

				var inputs = await _port.GetBulkCostInputsAsync(
						request.Context.Actor, request.NodeIds, request.AsOf, MaxHierarchyNodeCount, cancellationToken)
					.ConfigureAwait(false);

				// ADR 0040: a candidate is only costable if the actor is admin/cost-viewer or owns the
				// node or one of its ancestors -- walked from the one already-materialized snapshot, so
				// this adds no further round trips no matter how many candidates the page holds.
				var authorizedNodeIds = request.NodeIds
					.Where(nodeId => inputs.NodesById.ContainsKey(nodeId)
									 && CostAccessPolicy.CanView(
										 inputs.ActorRoles,
										 OwnsNodeOrAncestor(nodeId, request.Context.Actor, inputs.NodesById, inputs.OwnerUserIdsById)))
					.ToArray();

				var exactCosts = new Dictionary<JobNodeId, Money>();
				foreach (var worker in inputs.Workers) {
					var allocations = CostSegmentPartitioner.Partition(
						worker.Sessions, worker.EffectiveWorkingIntervals, inputs.NodesById,
						worker.Exceptions, worker.NodeOverrides, worker.UserCostRates, inputs.Bounds);
					var leafCosts = CostEngine.ComputeLeafCosts(
						allocations, inputs.NodesById, worker.Exceptions, worker.NodeOverrides, worker.UserCostRates, worker.UserDefaultRate);

					foreach (var nodeId in authorizedNodeIds) {
						var contribution = HierarchicalCostAggregator.Aggregate(nodeId, inputs.NodesById, leafCosts)
							.GetValueOrDefault(nodeId, new(0m));
						exactCosts[nodeId] = new(exactCosts.GetValueOrDefault(nodeId, new(0m)).Amount + contribution.Amount);
					}
				}

				var displayedCosts = authorizedNodeIds.ToDictionary(
					nodeId => nodeId, nodeId => exactCosts.GetValueOrDefault(nodeId, new(0m)).RoundToPennies());

				return new BulkNodeCostResult { DisplayedCosts = EquatableDictionaryFactory.CopyOf(displayedCosts) };
			});

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
