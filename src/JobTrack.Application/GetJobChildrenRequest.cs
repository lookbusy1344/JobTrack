namespace JobTrack.Application;

using Abstractions;
using Domain.Hierarchy;

/// <summary>
///     Input to <see cref="IJobQueries.GetJobChildrenAsync" />. Carries no ownership-based authorization
///     gate (see <see cref="GetJobNodeRequest" />) — <see cref="Ownership" /> is a plain result filter,
///     not an access restriction.
/// </summary>
public sealed record GetJobChildrenRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The parent whose direct children are requested.</summary>
	public required JobNodeId ParentId { get; init; }

	/// <summary>Restricts the returned children by owner. Defaults to <see cref="OwnershipFilter.All" />.</summary>
	public OwnershipFilter Ownership { get; init; } = OwnershipFilter.All;

	/// <summary>The archive scope of the returned children. Defaults to <see cref="JobArchiveFilter.ActiveOnly" />.</summary>
	public JobArchiveFilter ArchiveFilter { get; init; } = JobArchiveFilter.ActiveOnly;

	/// <summary>
	///     Zero-based number of matching children (ordered by <c>Id</c>) to skip before returning
	///     results. Must be non-negative.
	/// </summary>
	public int Offset { get; init; }

	/// <summary>
	///     Maximum number of children to return, or <see langword="null" /> for every match (the
	///     unbounded shape every in-process caller relied on before the external API's bounded-collection
	///     remediation). Must be positive when set; the external API layer always sets this.
	/// </summary>
	public int? Limit { get; init; }
}
