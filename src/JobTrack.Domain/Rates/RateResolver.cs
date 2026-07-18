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
		HourlyRate? userDefaultRate)
	{
		var overtimeException = exceptions.FirstOrDefault(exception => exception.Effect switch {
			ScheduleExceptionEffect.None => false,
			ScheduleExceptionEffect.AddWorkingTime =>
				exception.RateOverride is not null && exception.Interval.Contains(at),
			ScheduleExceptionEffect.RemoveWorkingTime => false,
			_ => throw new ArgumentOutOfRangeException(nameof(exceptions), exception.Effect, "Unknown schedule exception effect."),
		});
		if (overtimeException is not null) {
			return new(overtimeException.RateOverride!.Value, RateSource.OvertimeException);
		}

		var overridesByNode = nodeOverrides.GroupBy(over => over.NodeId).ToDictionary(group => group.Key, group => group.ToList());
		JobNodeId? currentId = nodeId;
		while (currentId is { } id) {
			if (overridesByNode.TryGetValue(id, out var candidates)) {
				var effective = candidates.FirstOrDefault(over => over.IsEffectiveAt(at));
				if (effective is not null) {
					return new(effective.Rate, RateSource.NodeOverride);
				}
			}

			currentId = HierarchyNodeLookup.GetRequired(nodesById, id).ParentId;
		}

		var userRate = userCostRates.FirstOrDefault(rate => rate.IsEffectiveAt(at));
		if (userRate is not null) {
			return new(userRate.Rate, RateSource.UserCostRate);
		}

		if (userDefaultRate is { } defaultRate) {
			return new(defaultRate, RateSource.UserDefault);
		}

		throw new MissingRateException($"No rate resolves for node {nodeId.Value} at {at}.");
	}
}
