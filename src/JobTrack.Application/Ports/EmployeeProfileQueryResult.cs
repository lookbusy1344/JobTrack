namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     Result of <see cref="IEmployeeQueryPort.GetEmployeeProfileAsync" />. Carries the actor's current
///     roles alongside the target profile so <see cref="JobQueries" /> can apply
///     <see cref="Domain.Authorization.EmployeeAccessPolicy" /> without a second round-trip — every
///     operation reloads authoritative roles fresh rather than trusting cached claims (plan §7.5).
/// </summary>
internal sealed record EmployeeProfileQueryResult
{
	/// <summary>The acting user's currently assigned roles.</summary>
	public required EquatableArray<EmployeeRole> ActorRoles { get; init; }

	/// <summary>The target employee's profile.</summary>
	public required EmployeeProfileResult Profile { get; init; }
}
