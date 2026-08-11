namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>Input to <see cref="ICostQueries.GetHierarchyTotalsAsync" />.</summary>
public sealed record GetHierarchyTotalsRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The root of the subtree to total.</summary>
	public required JobNodeId NodeId { get; init; }

	/// <summary>The instant costing is evaluated as of (spec §10: costs are calculated dynamically).</summary>
	public required Instant AsOf { get; init; }

	/// <summary>
	///     Maximum number of subtree nodes to include. <see langword="null" /> uses the application
	///     default; explicit values must be positive and no larger than the default maximum.
	/// </summary>
	public int? MaxHierarchyNodes { get; init; }
}
