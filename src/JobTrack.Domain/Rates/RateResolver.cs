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
		Resolve(nodeId, at, nodesById, exceptions, IndexOverridesByNode(nodeOverrides), userCostRates, userDefaultRate);

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
	///     Resolves against an override index already built by <see cref="IndexOverridesByNode" />.
	/// </summary>
	/// <exception cref="MissingRateException">No rate source applies.</exception>
	internal static ResolvedRate Resolve(
		JobNodeId nodeId,
		Instant at,
		IReadOnlyDictionary<JobNodeId, HierarchyNode> nodesById,
		IReadOnlyCollection<ScheduleExceptionEntry> exceptions,
		IReadOnlyDictionary<JobNodeId, List<NodeRateOverride>> overridesByNode,
		IReadOnlyCollection<UserCostRate> userCostRates,
		HourlyRate? userDefaultRate)
	{
		foreach (var exception in exceptions) {
			var priced = exception.Effect switch {
				ScheduleExceptionEffect.None => false,
				ScheduleExceptionEffect.AddWorkingTime =>
					exception.RateOverride is not null && exception.Interval.Contains(at),
				ScheduleExceptionEffect.RemoveWorkingTime => false,
				_ => throw new ArgumentOutOfRangeException(nameof(exceptions), exception.Effect, "Unknown schedule exception effect."),
			};
			if (priced) {
				return new(exception.RateOverride!.Value, RateSource.OvertimeException);
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
