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

	/// <summary>
	///     When non-blank, restricts to leaves whose description contains this text (case insensitive).
	///     Unlike <see cref="SearchJobNodesRequest" />, this scopes the dashboard's own
	///     owner/subtree-filtered candidate set, not a whole-tree query.
	/// </summary>
	public string? SearchText { get; init; }

	/// <summary>
	///     Zero-based number of matching leaves (in the calculator's own priority/deadline order) to skip
	///     before returning results. Must be non-negative.
	/// </summary>
	public int Offset { get; init; }

	/// <summary>
	///     Maximum number of leaves to return. An omitted value uses
	///     <see cref="AwaitingProgressPaging.DefaultPageSize" />; larger values are clamped to
	///     <see cref="AwaitingProgressPaging.MaxPageSize" />. Must be positive when set.
	/// </summary>
	public int? Limit { get; init; }
}
