namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobCommands.PickUpAsync" />. Claims an unassigned node from the pool
///     (ownership model §4.3) — unlike the other structural commands, this carries no caller-supplied
///     optimistic-concurrency version: the conditional <c>WHERE owner_user_id IS NULL</c> update is
///     itself the concurrency mechanism (a lost race throws <c>job-node-already-claimed</c>, not a
///     stale-version conflict).
/// </summary>
public sealed record PickUpJobNodeRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The node being claimed.</summary>
	public required JobNodeId NodeId { get; init; }
}
