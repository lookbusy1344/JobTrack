namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobQueries.GetScheduleAsync" />. The actor may always view their own
///     schedule; viewing another employee's requires <see cref="EmployeeRole.Administrator" /> (see
///     <see cref="Domain.Authorization.ScheduleAccessPolicy" />), matching the write-side rule.
/// </summary>
public sealed record GetScheduleRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The employee whose schedule versions and exceptions are requested.</summary>
	public required AppUserId UserId { get; init; }
}
