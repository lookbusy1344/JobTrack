namespace JobTrack.Domain.Costing;

using Intervals;

/// <summary>
///     Determines whether a cost segment falls within a worker's base scheduled working intervals
///     (before schedule exceptions), for trace eligibility stamping.
/// </summary>
internal static class WorkingTimeEligibility
{
	/// <summary>
	///     Stamps one segment against an index built once for the whole trace. Takes the index rather
	///     than the raw collection because the caller stamps every trace entry against the same
	///     unchanging set — rebuilding or rescanning it per entry is what made this
	///     <c>O(segments x intervals)</c>.
	/// </summary>
	internal static bool IsScheduledWorkingTime(WorkInterval segment, IntervalIndex scheduledWorkingIntervals) =>
		scheduledWorkingIntervals.Intersects(segment);
}
