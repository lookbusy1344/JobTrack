namespace JobTrack.Domain.Schedules;

using Intervals;

/// <summary>
///     Derives a user's effective working set (spec §8.3):
///     <c>(scheduled intervals union additive exceptions) minus subtractive exceptions</c>. Subtractive
///     exceptions take precedence wherever they overlap an additive exception, and every resulting
///     interval is normalized so no instant is counted twice.
/// </summary>
public static class ScheduleExceptionResolver
{
	/// <summary>
	///     Applies every <paramref name="exceptions" /> entry to <paramref name="scheduledIntervals" />,
	///     returning the resulting effective working set.
	/// </summary>
	public static IReadOnlyList<WorkInterval> Apply(IEnumerable<WorkInterval> scheduledIntervals, IEnumerable<ScheduleExceptionEntry> exceptions)
	{
		var materialized = exceptions is IReadOnlyCollection<ScheduleExceptionEntry> collection ? collection : exceptions.ToList();

		var additive = new List<WorkInterval>(scheduledIntervals);
		var subtractive = new List<WorkInterval>();
		foreach (var exception in materialized) {
			switch (exception.Effect) {
				case ScheduleExceptionEffect.None:
					break;
				case ScheduleExceptionEffect.AddWorkingTime:
					additive.Add(exception.Interval);
					break;
				case ScheduleExceptionEffect.RemoveWorkingTime:
					subtractive.Add(exception.Interval);
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(exceptions), exception.Effect, "Unknown schedule exception effect.");
			}
		}

		return IntervalAlgebra.Subtract(IntervalAlgebra.Normalize(additive), subtractive);
	}
}
