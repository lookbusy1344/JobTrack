namespace JobTrack.Application;

using Abstractions;
using Domain.Schedules;
using NodaTime;

/// <summary>Result of <see cref="IScheduleCommands.AddScheduleVersionAsync" />.</summary>
public sealed record ScheduleVersionResult
{
	/// <summary>The schedule version's identifier.</summary>
	public required ScheduleVersionId Id { get; init; }

	/// <summary>The employee this schedule version belongs to.</summary>
	public required AppUserId UserId { get; init; }

	/// <summary>The schedule version's effective range, zone, and weekly intervals.</summary>
	public required ScheduleVersion Schedule { get; init; }

	/// <summary>The instant this schedule version was last changed.</summary>
	public required Instant ChangedAt { get; init; }

	/// <summary>The schedule version's optimistic-concurrency version.</summary>
	public required long Version { get; init; }
}
