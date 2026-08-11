namespace JobTrack.Application;

using Abstractions;
using Domain.Schedules;

/// <summary>
///     Input to <see cref="IScheduleCommands.AddScheduleExceptionAsync" /> (spec §8.3). Reuses the pure
///     <see cref="ScheduleExceptionEntry" /> domain value directly — its own constructor already
///     enforces that a rate override is only ever carried by an <see cref="ScheduleExceptionEffect.AddWorkingTime" />
///     exception.
/// </summary>
public sealed record AddScheduleExceptionRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The employee this exception applies to.</summary>
	public required AppUserId UserId { get; init; }

	/// <summary>The exception's effect, interval, and optional rate override.</summary>
	public required ScheduleExceptionEntry Entry { get; init; }

	/// <summary>Why this exception is being recorded.</summary>
	public required string Reason { get; init; }
}
