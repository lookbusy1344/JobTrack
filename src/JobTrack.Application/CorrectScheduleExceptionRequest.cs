namespace JobTrack.Application;

using Abstractions;
using Domain.Schedules;

/// <summary>
///     Input to <see cref="IScheduleCommands.CorrectScheduleExceptionAsync" /> (ADR 0003: historical
///     schedule exceptions may be corrected in place by a Job manager or Administrator). Reuses the pure
///     <see cref="ScheduleExceptionEntry" /> domain value directly, the same shape as
///     <see cref="AddScheduleExceptionRequest" />.
/// </summary>
public sealed record CorrectScheduleExceptionRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The schedule exception being corrected.</summary>
	public required ScheduleExceptionId ExceptionId { get; init; }

	/// <summary>
	///     Optional nested-route cross-check: if set and it does not match the loaded row's owner, the
	///     correction is treated as if the row does not exist (mirrors <see cref="CorrectSessionRequest.LeafWorkId" />).
	/// </summary>
	public AppUserId? UserId { get; init; }

	/// <summary>The expected current optimistic-concurrency version.</summary>
	public required long Version { get; init; }

	/// <summary>Why this schedule exception is being corrected.</summary>
	public required string Reason { get; init; }

	/// <summary>The corrected effect, interval, and optional rate override.</summary>
	public required ScheduleExceptionEntry Entry { get; init; }
}
