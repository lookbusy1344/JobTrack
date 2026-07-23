namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     Result of <see cref="IScheduleQueryPort.GetScheduleAsync" />. Carries the actor's current roles
///     alongside the target employee's schedule snapshot so <see cref="JobQueries" /> can apply
///     <see cref="Domain.Authorization.ScheduleAccessPolicy" /> without a second round-trip.
/// </summary>
internal sealed record ScheduleQueryResult
{
	/// <summary>The acting user's currently assigned roles.</summary>
	public required EquatableArray<EmployeeRole> ActorRoles { get; init; }

	/// <summary>The target employee's schedule versions.</summary>
	public required EquatableArray<ScheduleVersionResult> Versions { get; init; }

	/// <summary>The target employee's schedule exceptions.</summary>
	public required EquatableArray<ScheduleExceptionResult> Exceptions { get; init; }
}
