namespace JobTrack.Web;

using Abstractions;
using Domain.Costing;

/// <summary>
///     Sums a <see cref="CostDetailsResult.Trace" />'s per-segment entries into one cost and allocated
///     duration per <see cref="WorkSessionId" /> — the trace is canonically segment-by-segment (one
///     entry per concurrency-partitioned slice), so a session that ran through more than one such slice
///     needs its contributions folded together before it can be shown as a single table-cell figure.
/// </summary>
internal static class SessionCostAggregator
{
	/// <summary>
	///     Groups <paramref name="trace" /> by <see cref="CostSegmentTrace.SessionId" />, summing each
	///     session's exact unrounded contributions (rounded to pennies once here, the reporting boundary
	///     for this figure) and its allocated share of every segment it touched.
	/// </summary>
	internal static IReadOnlyDictionary<WorkSessionId, (Money Cost, AllocatedDuration Duration)> AggregateBySession(
		IEnumerable<CostSegmentTrace> trace)
	{
		ArgumentNullException.ThrowIfNull(trace);

		var amounts = new Dictionary<WorkSessionId, decimal>();
		var durations = new Dictionary<WorkSessionId, AllocatedDuration>();
		foreach (var entry in trace) {
			amounts[entry.SessionId] = amounts.GetValueOrDefault(entry.SessionId) + entry.UnroundedContribution.Amount;
			durations[entry.SessionId] = durations.GetValueOrDefault(entry.SessionId, AllocatedDuration.Zero)
												  .Add(AllocatedDuration.FromShare(entry.AllocatedDuration));
		}

		return amounts.ToDictionary(entry => entry.Key, entry => (new Money(entry.Value).RoundToPennies(), durations[entry.Key]));
	}
}
