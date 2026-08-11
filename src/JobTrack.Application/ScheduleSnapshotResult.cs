namespace JobTrack.Application;

using Abstractions;

/// <summary>Result of <see cref="IJobQueries.GetScheduleAsync" />: an employee's schedule versions and exceptions.</summary>
public sealed record ScheduleSnapshotResult
{
	/// <summary>The employee's effective-dated schedule versions.</summary>
	public required EquatableArray<ScheduleVersionResult> Versions { get; init; }

	/// <summary>The employee's dated schedule exceptions.</summary>
	public required EquatableArray<ScheduleExceptionResult> Exceptions { get; init; }
}
