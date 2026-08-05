namespace JobTrack.Application;

using Abstractions;

/// <summary>A request holding area anchored inside a subtree, blocking its deletion (ADR 0061).</summary>
public sealed record SubtreeImpactHoldingArea
{
	/// <summary>The holding area's identifier.</summary>
	public required RequestHoldingAreaId Id { get; init; }

	/// <summary>The holding area's name.</summary>
	public required string Name { get; init; }

	/// <summary>The node inside the subtree it is anchored at.</summary>
	public required JobNodeId JobNodeId { get; init; }
}
