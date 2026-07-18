namespace JobTrack.Domain.Schedules;

using Abstractions;
using Intervals;

/// <summary>
///     Enforces the whole-set invariant of spec §8.3: for one user, two explicitly priced additive
///     exceptions must not overlap, so an overtime rate is never ambiguous. Unpriced additive
///     exceptions, subtractive exceptions, and merely adjacent priced exceptions are all unrestricted.
/// </summary>
public static class ScheduleExceptionValidator
{
	/// <summary>
	///     Checks every pair of priced <see cref="ScheduleExceptionEffect.AddWorkingTime" /> exceptions in
	///     <paramref name="exceptions" /> for an overlap.
	/// </summary>
	/// <exception cref="InvariantViolationException">Two priced additive exceptions overlap.</exception>
	public static void EnsureNoOverlappingPricedAdditiveExceptions(IReadOnlyList<ScheduleExceptionEntry> exceptions)
	{
		var priced = exceptions.Where(IsPricedAdditiveException).ToList();

		for (var i = 0; i < priced.Count; i++) {
			for (var j = i + 1; j < priced.Count; j++) {
				if (IntervalAlgebra.Overlaps(priced[i].Interval, priced[j].Interval)) {
					throw new InvariantViolationException(
						"schedule-exception.priced-additive-overlap",
						"Two explicitly priced additive schedule exceptions for the same user cannot overlap.");
				}
			}
		}
	}

	private static bool IsPricedAdditiveException(ScheduleExceptionEntry exception) =>
		exception.Effect switch {
			ScheduleExceptionEffect.None => false,
			ScheduleExceptionEffect.AddWorkingTime => exception.RateOverride is not null,
			ScheduleExceptionEffect.RemoveWorkingTime => false,
			_ => throw new ArgumentOutOfRangeException(nameof(exception), exception.Effect, "Unknown schedule exception effect."),
		};
}
