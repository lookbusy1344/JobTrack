namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobQueries.GetLeafSessionsAsync" />: one leaf's recorded sessions, either
///     every worker's or narrowed to a single worker. Any employee role may read the list — recorded
///     work is job data, which spec §7.3 makes viewable by every employee
///     (<see cref="Domain.Authorization.WorkSessionAccessPolicy.CanView" />, ADR 0041); editing a session
///     remains separately gated by node control (<c>CanManage</c>).
/// </summary>
public sealed record GetLeafSessionsRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The leaf whose sessions are requested (<c>leaf_work_id</c>, the leaf's <c>job_node_id</c>).</summary>
	public required JobNodeId LeafWorkId { get; init; }

	/// <summary>
	///     The single worker whose sessions on this leaf are requested, or <see langword="null" /> (the
	///     default) for every worker's — the whole record of work on the leaf, which is what a reader
	///     wants first; narrowing to one worker is the follow-up filter, not the entry point.
	/// </summary>
	public AppUserId? WorkedByUserId { get; init; }

	/// <summary>
	///     Zero-based number of sessions (ordered most-recent-first by <c>StartedAt</c>, then by
	///     <c>Id</c>) to skip before returning results. Must be non-negative.
	/// </summary>
	public int Offset { get; init; }

	/// <summary>
	///     Maximum number of sessions to return, or <see langword="null" /> for every session (the
	///     unbounded shape every in-process caller relied on before the external API's bounded-collection
	///     remediation). Must be positive when set; the external API layer always sets this.
	/// </summary>
	public int? Limit { get; init; }
}
