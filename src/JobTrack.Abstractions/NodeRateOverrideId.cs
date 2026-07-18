namespace JobTrack.Abstractions;

/// <summary>Strongly typed identifier for a <c>node_rate_override</c> row (ADR 0006).</summary>
public readonly record struct NodeRateOverrideId(long Value)
{
	/// <summary>Whether this identifier is unset.</summary>
	public bool IsUnspecified => Value == 0;
}
