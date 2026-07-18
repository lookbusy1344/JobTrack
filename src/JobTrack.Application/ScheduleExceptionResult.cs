namespace JobTrack.Application;

using Abstractions;
using Domain.Schedules;
using NodaTime;

/// <summary>Result of <see cref="IScheduleCommands.AddScheduleExceptionAsync" />.</summary>
public sealed record ScheduleExceptionResult
{
	/// <summary>The exception's identifier.</summary>
	public required ScheduleExceptionId Id { get; init; }

	/// <summary>The employee this exception applies to.</summary>
	public required AppUserId UserId { get; init; }

	/// <summary>The exception's effect, interval, and optional rate override.</summary>
	public required ScheduleExceptionEntry Entry { get; init; }

	/// <summary>Why this exception was recorded.</summary>
	public required string Reason { get; init; }

	/// <summary>The employee who recorded this exception.</summary>
	public required AppUserId CreatedBy { get; init; }

	/// <summary>The instant this exception was last changed.</summary>
	public required Instant ChangedAt { get; init; }

	/// <summary>The exception's optimistic-concurrency version.</summary>
	public required long Version { get; init; }
}
