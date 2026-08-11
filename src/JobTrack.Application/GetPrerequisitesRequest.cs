namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobQueries.GetPrerequisitesAsync" />. Carries no ownership-based
///     authorization gate, matching <see cref="GetReadinessRequest" /> — viewing job data, including
///     prerequisite edges, is an unqualified baseline capability for every role (spec §7.3).
/// </summary>
public sealed record GetPrerequisitesRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The node whose prerequisite edges (in either direction) are requested.</summary>
	public required JobNodeId NodeId { get; init; }

	/// <summary>
	///     Zero-based number of edges (ordered by <c>RequiredJobId</c>, then <c>DependentJobId</c>) to
	///     skip before returning results. Must be non-negative.
	/// </summary>
	public int Offset { get; init; }

	/// <summary>
	///     Maximum number of edges to return, or <see langword="null" /> for every edge (the unbounded
	///     shape every in-process caller relied on before the external API's bounded-collection
	///     remediation). Must be positive when set; the external API layer always sets this.
	/// </summary>
	public int? Limit { get; init; }
}
