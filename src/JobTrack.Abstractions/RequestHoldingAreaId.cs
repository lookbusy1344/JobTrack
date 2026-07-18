namespace JobTrack.Abstractions;

/// <summary>Strongly typed identifier for a <c>request_holding_area</c> row (ADR 0033).</summary>
public readonly record struct RequestHoldingAreaId(long Value)
{
	/// <summary>Whether this identifier is unset.</summary>
	public bool IsUnspecified => Value == 0;
}
