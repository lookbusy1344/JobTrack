namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     Result of <see cref="IEmployeeQueryPort.GetAccountStateAsync" />. Carries the actor's current
///     roles alongside the target account state so <see cref="JobQueries" /> can apply
///     <see cref="Domain.Authorization.EmployeeAccessPolicy" /> without a second round-trip.
/// </summary>
internal sealed record AccountStateQueryResult
{
	/// <summary>The acting user's currently assigned roles.</summary>
	public required EquatableArray<EmployeeRole> ActorRoles { get; init; }

	/// <summary>The target employee's account state.</summary>
	public required AccountStateResult AccountState { get; init; }
}
