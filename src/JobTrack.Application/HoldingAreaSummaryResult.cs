namespace JobTrack.Application;

using Abstractions;

/// <summary>One holding area a requester is currently eligible to submit into (ADR 0033, plan §3).</summary>
public sealed record HoldingAreaSummaryResult
{
	/// <summary>The holding area's identifier.</summary>
	public required RequestHoldingAreaId Id { get; init; }

	/// <summary>The holding area's display name.</summary>
	public required string Name { get; init; }

	/// <summary>
	///     The description of the job node this holding area anchors requests under. A requester
	///     chooses between the job nodes their request will hang from, so the requester-facing surface
	///     names each option by this rather than by the staff-configured <see cref="Name" />.
	/// </summary>
	public required string JobNodeDescription { get; init; }
}
