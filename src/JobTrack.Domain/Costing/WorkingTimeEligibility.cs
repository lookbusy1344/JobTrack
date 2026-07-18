namespace JobTrack.Domain.Costing;

using Intervals;

/// <summary>
///     Determines whether a cost segment falls within a worker's base scheduled working intervals
///     (before schedule exceptions), for trace eligibility stamping.
/// </summary>
internal static class WorkingTimeEligibility
{
	internal static bool IsScheduledWorkingTime(
		WorkInterval segment, IReadOnlyCollection<WorkInterval> scheduledWorkingIntervals) =>
		scheduledWorkingIntervals.Any(scheduled => IntervalAlgebra.Intersect(segment, scheduled) is not null);
}
