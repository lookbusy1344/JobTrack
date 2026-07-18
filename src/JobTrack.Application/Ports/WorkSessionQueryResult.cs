namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     Result of <see cref="IWorkSessionQueryPort.GetSessionsAsync" />. Carries the actor's current
///     roles alongside the target worker's sessions so <see cref="JobQueries" /> can apply
///     <see cref="Domain.Authorization.WorkSessionAccessPolicy" /> without a second round-trip.
/// </summary>
public sealed record WorkSessionQueryResult
{
	/// <summary>The acting user's currently assigned roles.</summary>
	public required EquatableArray<EmployeeRole> ActorRoles { get; init; }

	/// <summary>The worker's sessions on the leaf, most recent first.</summary>
	public required EquatableArray<WorkSessionResult> Sessions { get; init; }
}
