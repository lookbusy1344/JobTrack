namespace JobTrack.Domain.Schedules;

/// <summary>
///     The effect of a <see cref="ScheduleExceptionEntry" /> on a user's effective working set (spec §8.3).
/// </summary>
public enum ScheduleExceptionEffect
{
	/// <summary>No effect has been specified yet.</summary>
	None = 0,

	/// <summary>Adds working time outside ordinary scheduled hours, e.g. overtime.</summary>
	AddWorkingTime = 1,

	/// <summary>Removes working time from ordinary scheduled hours, e.g. leave or a holiday.</summary>
	RemoveWorkingTime = 2,
}
