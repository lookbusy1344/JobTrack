namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>Input to <see cref="ICostQueries.GetCostDetailsAsync" />.</summary>
public sealed record GetCostDetailsRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The node to calculate the cost of.</summary>
	public required JobNodeId NodeId { get; init; }

	/// <summary>The instant costing is evaluated as of (spec §10: costs are calculated dynamically).</summary>
	public required Instant AsOf { get; init; }

	/// <summary>
	///     Maximum number of rate-provenance trace segments to return. <see langword="null" /> uses the
	///     application default; explicit values must be positive and no larger than the default maximum.
	/// </summary>
	public int? MaxTraceSegments { get; init; }
}
