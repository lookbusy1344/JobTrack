namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     Result of <see cref="ICostQueryPort.GetCostAccessInputsAsync" /> (2026-07-25 scalability-follow-up
///     plan §2.4): the actor's current roles and the queried node's ancestor-chain owners, from one
///     snapshot, so <see cref="CostQueries" /> can apply <see cref="Domain.Authorization.CostAccessPolicy" />
///     before materializing worker sessions/rate data without two separate round trips.
/// </summary>
internal sealed record CostAccessInputs
{
	/// <summary>The acting user's currently assigned roles.</summary>
	public required EquatableArray<EmployeeRole> ActorRoles { get; init; }

	/// <summary>The queried node's owner and every ancestor's owner, skipping unassigned nodes on the path.</summary>
	public required EquatableArray<AppUserId> AncestorOwnerIds { get; init; }
}
