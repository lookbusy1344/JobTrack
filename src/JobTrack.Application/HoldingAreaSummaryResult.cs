namespace JobTrack.Application;

using Abstractions;

/// <summary>One holding area a requester is currently eligible to submit into (ADR 0033, plan §3).</summary>
public sealed record HoldingAreaSummaryResult
{
	/// <summary>The holding area's identifier.</summary>
	public required RequestHoldingAreaId Id { get; init; }

	/// <summary>The holding area's display name.</summary>
	public required string Name { get; init; }
}
