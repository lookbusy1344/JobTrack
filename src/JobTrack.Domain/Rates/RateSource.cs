namespace JobTrack.Domain.Rates;

/// <summary>
///     The provenance of a resolved hourly rate under the precedence order of spec §9.3, in
///     precedence order from highest to lowest.
/// </summary>
public enum RateSource
{
	/// <summary>No rate source has been resolved yet.</summary>
	None = 0,

	/// <summary>An explicit rate on an effective <c>AddWorkingTime</c> schedule exception covering the instant.</summary>
	OvertimeException = 1,

	/// <summary>The nearest node or ancestor override effective for the worker at the instant.</summary>
	NodeOverride = 2,

	/// <summary>The user's effective-dated <c>UserCostRate</c>.</summary>
	UserCostRate = 3,

	/// <summary>The user's default rate.</summary>
	UserDefault = 4,
}
