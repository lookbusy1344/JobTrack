namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     Result of <see cref="IRateQueryPort.GetRatesAsync" />. Carries the actor's current roles
///     alongside the target employee's rate snapshot so <see cref="JobQueries" /> can apply
///     <see cref="Domain.Authorization.CostAccessPolicy" /> without a second round-trip.
/// </summary>
internal sealed record RateQueryResult
{
	/// <summary>The acting user's currently assigned roles.</summary>
	public required EquatableArray<EmployeeRole> ActorRoles { get; init; }

	/// <summary>The target employee's user cost rates.</summary>
	public required EquatableArray<UserCostRateResult> UserCostRates { get; init; }

	/// <summary>The target employee's node rate overrides.</summary>
	public required EquatableArray<NodeRateOverrideResult> NodeRateOverrides { get; init; }
}
