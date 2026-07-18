namespace JobTrack.Application;

using Abstractions;
using Domain.Schedules;

/// <summary>
///     Input to <see cref="IScheduleCommands.CorrectScheduleVersionAsync" /> (ADR 0003: historical
///     schedule versions may be corrected in place by a Job manager or Administrator). Reuses the pure
///     <see cref="ScheduleVersion" /> domain value directly, the same shape as
///     <see cref="AddScheduleVersionRequest" />.
/// </summary>
public sealed record CorrectScheduleVersionRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The schedule version being corrected.</summary>
	public required ScheduleVersionId VersionId { get; init; }

	/// <summary>
	///     Optional nested-route cross-check: if set and it does not match the loaded row's owner, the
	///     correction is treated as if the row does not exist (mirrors <see cref="CorrectSessionRequest.LeafWorkId" />).
	/// </summary>
	public AppUserId? UserId { get; init; }

	/// <summary>The expected current optimistic-concurrency version.</summary>
	public required long Version { get; init; }

	/// <summary>Why this schedule version is being corrected.</summary>
	public required string Reason { get; init; }

	/// <summary>The corrected effective range, zone, and weekly intervals.</summary>
	public required ScheduleVersion Schedule { get; init; }
}
