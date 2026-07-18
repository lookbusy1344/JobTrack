namespace JobTrack.Application;

using Abstractions;
using Domain.Hierarchy;

/// <summary>
///     Input to <see cref="IJobQueries.GetAwaitingProgressAsync" />. Carries no ownership-based
///     authorization gate (see <see cref="GetJobNodeRequest" />) — <see cref="Ownership" /> is a plain
///     result filter, not an access restriction.
/// </summary>
public sealed record GetAwaitingProgressRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>Restricts the returned leaves by owner. Defaults to <see cref="OwnershipFilter.All" />.</summary>
	public OwnershipFilter Ownership { get; init; } = OwnershipFilter.All;

	/// <summary>When set, only leaves within this node's subtree (inclusive) are returned; otherwise the whole tree.</summary>
	public JobNodeId? SubtreeRootId { get; init; }
}
