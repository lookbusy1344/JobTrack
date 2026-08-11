namespace JobTrack.Application;

using Abstractions;
using Domain.Schedules;

/// <summary>
///     Input to <see cref="IScheduleCommands.AddScheduleVersionAsync" />. Reuses the pure
///     <see cref="ScheduleVersion" /> domain value directly — its own constructor already enforces that
///     <c>EffectiveEnd</c> strictly follows <c>EffectiveStart</c> and that each <c>WeeklyInterval</c> is
///     individually well-formed, so this request cannot carry a structurally invalid schedule.
/// </summary>
public sealed record AddScheduleVersionRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The employee this schedule version belongs to.</summary>
	public required AppUserId UserId { get; init; }

	/// <summary>The schedule version being added.</summary>
	public required ScheduleVersion Schedule { get; init; }
}
