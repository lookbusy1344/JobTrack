namespace JobTrack.Application;

using Abstractions;

/// <summary>Result of <see cref="IJobQueries.GetRatesAsync" />: an employee's cost rates and node rate overrides.</summary>
public sealed record RateSnapshotResult
{
	/// <summary>The employee's effective-dated user cost rates.</summary>
	public required EquatableArray<UserCostRateResult> UserCostRates { get; init; }

	/// <summary>The employee's effective-dated node rate overrides.</summary>
	public required EquatableArray<NodeRateOverrideResult> NodeRateOverrides { get; init; }
}
