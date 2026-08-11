namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     Result of <see cref="IWorkSessionQueryPort.GetManageCapabilitiesAsync" />. Carries the actor's
///     current roles alongside which of the requested leaves the actor directly owns or has an owning
///     ancestor of, so <see cref="JobQueries" /> can apply
///     <see cref="Domain.Authorization.WorkSessionAccessPolicy.CanManage" /> per leaf without a second
///     round-trip.
/// </summary>
internal sealed record WorkSessionManageCapabilityQueryResult
{
	/// <summary>The acting user's currently assigned roles.</summary>
	public required EquatableArray<EmployeeRole> ActorRoles { get; init; }

	/// <summary>The subset of the requested leaves the actor directly owns or has an owning ancestor of.</summary>
	public required EquatableArray<JobNodeId> ControlledLeafWorkIds { get; init; }
}
