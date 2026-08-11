namespace JobTrack.Domain.Schedules;

using Abstractions;
using Intervals;

/// <summary>
///     One user-specific dated instant range that adds to or removes from a user's normal working
///     intervals (spec §8.3). Only an <see cref="ScheduleExceptionEffect.AddWorkingTime" /> exception
///     may carry an explicit <see cref="RateOverride" />, which applies only inside its interval and
///     takes precedence over node overrides, effective-dated user rates, and the user's default rate.
/// </summary>
public sealed record ScheduleExceptionEntry
{
	/// <summary>Creates a <see cref="ScheduleExceptionEntry" /> value.</summary>
	/// <exception cref="ArgumentException"><paramref name="rateOverride" /> is set on a <see cref="ScheduleExceptionEffect.RemoveWorkingTime" /> exception.</exception>
	public ScheduleExceptionEntry(ScheduleExceptionEffect effect, WorkInterval interval, HourlyRate? rateOverride)
	{
		switch (effect) {
			case ScheduleExceptionEffect.AddWorkingTime:
			case ScheduleExceptionEffect.RemoveWorkingTime:
				break;
			case ScheduleExceptionEffect.None:
			default:
				throw new ArgumentOutOfRangeException(nameof(effect), effect, "Schedule exception effect must add or remove working time.");
		}

		if (rateOverride is not null && effect != ScheduleExceptionEffect.AddWorkingTime) {
			throw new ArgumentException("A rate override is only valid on an AddWorkingTime exception.", nameof(rateOverride));
		}

		Effect = effect;
		Interval = interval;
		RateOverride = rateOverride;
	}

	/// <summary>Whether this exception adds or removes working time.</summary>
	public ScheduleExceptionEffect Effect { get; }

	/// <summary>The instant range this exception covers.</summary>
	public WorkInterval Interval { get; }

	/// <summary>
	///     The explicit overtime rate for this exception, if any. Always <see langword="null" /> for
	///     <see cref="ScheduleExceptionEffect.RemoveWorkingTime" />.
	/// </summary>
	public HourlyRate? RateOverride { get; }
}
