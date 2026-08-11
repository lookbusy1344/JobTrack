namespace JobTrack.Application;

using Abstractions;
using Domain.Hierarchy;
using NodaTime;

/// <summary>
///     Input to <see cref="IJobQueries.GetJobSubtreeAsync" />. Carries no ownership-based authorization
///     gate on the structural fetch (see <see cref="GetJobNodeRequest" />) -- only <see cref="JobSubtreeResult.RootTotal" />/
///     <see cref="JobSubtreeNodeResult.Cost" /> are individually gated by
///     <see
///         cref="Domain.Authorization.CostAccessPolicy" />
///     (ADR 0040).
/// </summary>
public sealed record GetJobSubtreeRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The subtree root.</summary>
	public required JobNodeId RootId { get; init; }

	/// <summary>
	///     Levels below <see cref="RootId" /> to render. <see langword="null" /> uses
	///     <see cref="JobSubtreeLimits.DefaultMaxDepth" />; an explicit value must be between 0 and
	///     <see cref="JobSubtreeLimits.HardMaxDepth" /> (ADR 0039 decision 1).
	/// </summary>
	public int? MaxDepth { get; init; }

	/// <summary>Restricts which nodes match for highlighting. Defaults to <see cref="OwnershipFilter.All" />.</summary>
	public OwnershipFilter Ownership { get; init; } = OwnershipFilter.All;

	/// <summary>The archive scope of matching nodes. Defaults to <see cref="JobArchiveFilter.ActiveOnly" />.</summary>
	public JobArchiveFilter ArchiveFilter { get; init; } = JobArchiveFilter.ActiveOnly;

	/// <summary>The instant costing is evaluated as of (spec §10: costs are calculated dynamically).</summary>
	public required Instant AsOf { get; init; }
}
