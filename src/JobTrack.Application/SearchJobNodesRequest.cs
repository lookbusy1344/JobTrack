namespace JobTrack.Application;

using Domain.Hierarchy;

/// <summary>
///     Input to <see cref="IJobQueries.SearchJobNodesAsync" />. Carries no ownership-based authorization
///     gate (see <see cref="GetJobNodeRequest" />) — <see cref="Ownership" /> is a plain result filter,
///     not an access restriction.
/// </summary>
public sealed record SearchJobNodesRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The case-insensitive substring to match against a node's <c>Description</c>.</summary>
	public required string SearchText { get; init; }

	/// <summary>Restricts the returned matches by owner. Defaults to <see cref="OwnershipFilter.All" />.</summary>
	public OwnershipFilter Ownership { get; init; } = OwnershipFilter.All;

	/// <summary>The archive scope of the returned matches. Defaults to <see cref="JobArchiveFilter.ActiveOnly" />.</summary>
	public JobArchiveFilter ArchiveFilter { get; init; } = JobArchiveFilter.ActiveOnly;

	/// <summary>
	///     Zero-based number of matches (ordered by <c>Id</c>) to skip before returning results. Must
	///     be non-negative.
	/// </summary>
	public int Offset { get; init; }

	/// <summary>
	///     Maximum number of matches to return, or <see langword="null" /> for every match (the
	///     unbounded shape every in-process caller relied on before the external API's bounded-collection
	///     remediation). Must be positive when set; the external API layer always sets this.
	/// </summary>
	public int? Limit { get; init; }
}
