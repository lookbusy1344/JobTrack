namespace JobTrack.Domain.Concurrency;

using Abstractions;
using Intervals;

/// <summary>
///     One recorded work session, already clipped to a finite interval by the caller (an unfinished
///     session is bounded by <c>asOf</c>, exactly as <see cref="Costing.CostableSession" /> is), ready
///     for <see cref="ConcurrentWorkCalculator" /> to intersect against another of the same worker's
///     sessions. Carries the worker so the calculator never has to infer whose session it is from
///     collection order.
/// </summary>
public sealed record ConcurrentWorkSession(
	WorkSessionId SessionId,
	JobNodeId NodeId,
	AppUserId WorkedByUserId,
	WorkInterval Interval);
