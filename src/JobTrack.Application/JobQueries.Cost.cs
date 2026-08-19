namespace JobTrack.Application;

using Abstractions;
using Domain.Authorization;
using Domain.Costing;
using Domain.Hierarchy;
using NodaTime;

internal sealed partial class JobQueries
{
	private async Task<EquatableArray<JobNodeSummaryResult>> EnrichSummariesWithCostAsync(
		CommandContext context, EquatableArray<JobNodeSummaryResult> summaries, CancellationToken cancellationToken)
	{
		if (summaries.Count == 0) {
			return summaries;
		}

		var asOf = _clock.GetCurrentInstant();
		// ADR 0042: another worker's individual leaf cost stays hidden even where the actor is
		// admitted to the node; a branch's roll-up is an aggregate and remains visible.
		var costRoles = await GetCostFilterRolesAsync(context.Actor, cancellationToken).ConfigureAwait(false);
		var candidateIds = summaries
						   .Where(summary => CostAccessPolicy.CanViewNodeCost(costRoles, summary.HasChildren, summary.OwnerUserId, context.Actor))
						   .Select(summary => summary.Id)
						   .ToArray();

		// Fresh-eyes review §2.8: one bulk snapshot for the whole page, never one round trip per row.
		var metrics = await GetBulkCostMetricsAsync(context, candidateIds, asOf, cancellationToken).ConfigureAwait(false);

		return [
			.. summaries.Select(summary => summary with {
				Cost = metrics.Costs.GetValueOrDefault(summary.Id), AllocatedDuration = metrics.Durations.GetValueOrDefault(summary.Id),
			}),
		];
	}

	private async Task<EquatableArray<AwaitingProgressEntry>> EnrichAwaitingProgressWithCostAsync(
		CommandContext context, EquatableArray<AwaitingProgressEntry> entries, CancellationToken cancellationToken)
	{
		if (entries.Count == 0) {
			return entries;
		}

		var asOf = _clock.GetCurrentInstant();
		// Awaiting-progress entries are leaves by construction, so the branch-aggregate relief in
		// CanViewNodeCost never applies here: it reduces to "your own or unassigned" (ADR 0042).
		var costRoles = await GetCostFilterRolesAsync(context.Actor, cancellationToken).ConfigureAwait(false);
		var candidateIds = entries
						   .Where(entry => CostAccessPolicy.CanViewNodeCost(costRoles, false, entry.OwnerUserId, context.Actor))
						   .Select(entry => entry.Id)
						   .ToArray();

		var metrics = await GetBulkCostMetricsAsync(context, candidateIds, asOf, cancellationToken).ConfigureAwait(false);

		return [
			.. entries.Select(entry => entry with {
				Cost = metrics.Costs.GetValueOrDefault(entry.Id), AllocatedDuration = metrics.Durations.GetValueOrDefault(entry.Id),
			}),
		];
	}

	/// <summary>
	///     Prices every candidate in one bulk call (fresh-eyes review §2.8) instead of one
	///     <see cref="ICostQueries.GetHierarchyTotalsAsync" /> round trip per row. Cost is an optional
	///     field on an otherwise universally browsable listing (ADR 0039 decision 4), so a failure here
	///     degrades to "no costs shown" rather than failing the whole listing.
	/// </summary>
	private async Task<(
		EquatableDictionary<JobNodeId, Money> Costs,
		EquatableDictionary<JobNodeId, AllocatedDuration> Durations)> GetBulkCostMetricsAsync(
		CommandContext context, JobNodeId[] candidateIds, Instant asOf, CancellationToken cancellationToken)
	{
		if (candidateIds.Length == 0) {
			return (
				EquatableDictionaryFactory.CopyOf(new Dictionary<JobNodeId, Money>()),
				EquatableDictionaryFactory.CopyOf(new Dictionary<JobNodeId, AllocatedDuration>()));
		}

		try {
			var displayed = new Dictionary<JobNodeId, Money>();
			var durations = new Dictionary<JobNodeId, AllocatedDuration>();
			// The bulk port rejects a candidate set wider than its cap. A listing page can legitimately
			// exceed it (a caller-supplied id set via GetJobSummariesAsync is not page-bounded), so chunk
			// to the cap and merge -- prices are per-node independent, so batching is exact. Overflowing
			// the port in one call and swallowing the resulting ArgumentOutOfRangeException would blank
			// every row's cost instead, which is why that is no longer caught below.
			foreach (var batch in candidateIds.Chunk(CostQueries.MaxBulkNodeIdCount)) {
				var result = await _costQueries.GetBulkNodeCostsAsync(
					new() {
						Context = context,
						NodeIds = [.. batch],
						AsOf = asOf,
					}, cancellationToken).ConfigureAwait(false);
				foreach (var (nodeId, cost) in result.DisplayedCosts) {
					displayed[nodeId] = cost;
				}

				foreach (var (nodeId, duration) in result.AllocatedDurations) {
					durations[nodeId] = duration;
				}
			}

			return (EquatableDictionaryFactory.CopyOf(displayed), EquatableDictionaryFactory.CopyOf(durations));
		}
		catch (AuthorizationDeniedException) {
			return (
				EquatableDictionaryFactory.CopyOf(new Dictionary<JobNodeId, Money>()),
				EquatableDictionaryFactory.CopyOf(new Dictionary<JobNodeId, AllocatedDuration>()));
		}
		catch (MissingRateException) {
			return (
				EquatableDictionaryFactory.CopyOf(new Dictionary<JobNodeId, Money>()),
				EquatableDictionaryFactory.CopyOf(new Dictionary<JobNodeId, AllocatedDuration>()));
		}
	}

	/// <summary>
	///     The actor's roles for the per-node cost filter (ADR 0042). Cost is an optional field on an
	///     otherwise universally browsable listing, never a whole-request denial (ADR 0039 decision 4), so
	///     an actor whose roles cannot be resolved yields no roles — the most restrictive answer — rather
	///     than failing the listing outright.
	/// </summary>
	private async Task<EquatableArray<EmployeeRole>> GetCostFilterRolesAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		try {
			return await _employeeQueryPort.GetActorRolesAsync(actor, cancellationToken).ConfigureAwait(false);
		}
		catch (EntityNotFoundException) {
			return [];
		}
	}
}
