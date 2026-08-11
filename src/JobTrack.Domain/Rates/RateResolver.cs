namespace JobTrack.Domain.Rates;

using Abstractions;
using Hierarchy;
using NodaTime;
using Schedules;

/// <summary>
///     Resolves the applicable hourly rate for one worker at one costed instant on one node (spec
///     §9.3), applying the precedence order: an explicit rate on an effective priced additive schedule
///     exception; else the nearest node/ancestor override (spec §9.2's effective nearest-ancestor
///     rule); else the worker's effective-dated <see cref="UserCostRate" />; else the worker's default
///     rate. Every collection argument is assumed already scoped to the one worker being costed —
///     <see cref="RateResolver" /> has no concept of "which worker," only "which candidate rates."
/// </summary>
public static class RateResolver
{
	/// <summary>
	///     Resolves the rate applicable at <paramref name="at" /> for a session on <paramref name="nodeId" />.
	/// </summary>
	/// <exception cref="MissingRateException">No rate source applies.</exception>
	public static ResolvedRate Resolve(
		JobNodeId nodeId,
		Instant at,
		IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById,
		IReadOnlyCollection<ScheduleExceptionEntry> exceptions,
		IReadOnlyCollection<NodeRateOverride> nodeOverrides,
		IReadOnlyCollection<UserCostRate> userCostRates,
		HourlyRate? userDefaultRate) =>
		Resolve(nodeId, at, nodesById, FilterPricedExceptions(exceptions), IndexOverridesByNode(nodeOverrides), userCostRates, userDefaultRate);

	/// <summary>
	///     Keeps only the exceptions that can ever resolve a rate — priced
	///     <see cref="ScheduleExceptionEffect.AddWorkingTime" /> entries, the sole effect
	///     <see cref="ScheduleExceptionEntry" />'s own constructor permits a <see cref="ScheduleExceptionEntry.RateOverride" />
	///     on — preserving declaration order, since resolution takes the first effective entry it finds.
	///     A caller resolving many instants against one unchanging exception set — the cost engine
	///     resolves a rate per segment allocation, against a set that is overwhelmingly unpriced
	///     removals — filters once and passes the result to the internal overload rather than scanning
	///     the full list per resolution.
	/// </summary>
	internal static List<ScheduleExceptionEntry> FilterPricedExceptions(IReadOnlyCollection<ScheduleExceptionEntry> exceptions)
	{
		var priced = new List<ScheduleExceptionEntry>();
		foreach (var exception in exceptions) {
			var isPriced = exception.Effect switch {
				ScheduleExceptionEffect.None => false,
				ScheduleExceptionEffect.AddWorkingTime => exception.RateOverride is not null,
				ScheduleExceptionEffect.RemoveWorkingTime => false,
				_ => throw new ArgumentOutOfRangeException(nameof(exceptions), exception.Effect, "Unknown schedule exception effect."),
			};
			if (isPriced) {
				priced.Add(exception);
			}
		}

		return priced;
	}

	/// <summary>
	///     Groups <paramref name="nodeOverrides" /> by node once. A caller resolving many instants
	///     against one unchanging override set — the cost engine resolves a rate per segment allocation —
	///     builds this once and passes it to the internal overload rather than regrouping per
	///     resolution. Declaration order within a node is preserved, since resolution takes the first
	///     effective override it finds.
	/// </summary>
	internal static Dictionary<JobNodeId, List<NodeRateOverride>> IndexOverridesByNode(IReadOnlyCollection<NodeRateOverride> nodeOverrides)
	{
		var overridesByNode = new Dictionary<JobNodeId, List<NodeRateOverride>>();
		foreach (var nodeOverride in nodeOverrides) {
			if (!overridesByNode.TryGetValue(nodeOverride.NodeId, out var candidates)) {
				candidates = [];
				overridesByNode[nodeOverride.NodeId] = candidates;
			}

			candidates.Add(nodeOverride);
		}

		return overridesByNode;
	}

	/// <summary>
	///     Resolves against a priced-exception list already built by
	///     <see cref="FilterPricedExceptions" /> and an override index already built by
	///     <see cref="IndexOverridesByNode" />. The rate check itself re-verifies the priced shape, so
	///     an unfiltered list resolves identically — the filter is purely the per-resolution saving.
	/// </summary>
	/// <exception cref="MissingRateException">No rate source applies.</exception>
	internal static ResolvedRate Resolve(
		JobNodeId nodeId,
		Instant at,
		IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById,
		IReadOnlyList<ScheduleExceptionEntry> pricedExceptions,
		IReadOnlyDictionary<JobNodeId, List<NodeRateOverride>> overridesByNode,
		IReadOnlyCollection<UserCostRate> userCostRates,
		HourlyRate? userDefaultRate)
	{
		foreach (var exception in pricedExceptions) {
			if (exception.RateOverride is HourlyRate overtimeRate && exception.Interval.Contains(at)) {
				return new(overtimeRate, RateSource.OvertimeException);
			}
		}

		JobNodeId? currentId = nodeId;
		while (currentId is JobNodeId id) {
			if (overridesByNode.TryGetValue(id, out var candidates)) {
				foreach (var candidate in candidates) {
					if (candidate.IsEffectiveAt(at)) {
						return new(candidate.Rate, RateSource.NodeOverride);
					}
				}
			}

			currentId = HierarchyNodeLookup.GetRequired(nodesById, id).ParentId;
		}

		foreach (var userRate in userCostRates) {
			if (userRate.IsEffectiveAt(at)) {
				return new(userRate.Rate, RateSource.UserCostRate);
			}
		}

		if (userDefaultRate is HourlyRate defaultRate) {
			return new(defaultRate, RateSource.UserDefault);
		}

		throw new MissingRateException($"No rate resolves for node {nodeId.Value} at {at}.");
	}
}
