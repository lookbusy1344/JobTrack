namespace JobTrack.Abstractions;

/// <summary>Strongly typed identifier for a <c>user_cost_rate</c> row (ADR 0006).</summary>
public readonly record struct UserCostRateId(long Value)
{
	/// <summary>Whether this identifier is unset.</summary>
	public bool IsUnspecified => Value == 0;
}
