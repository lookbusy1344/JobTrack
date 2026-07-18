namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobQueries.GetActiveSessionsAsync" />. Scoped to the actor's own unfinished
///     sessions among the given leaves, mirroring <see cref="GetJobSummariesRequest" />'s batch-by-ids
///     shape — used by job-tree browsing to show an inline "active session" indicator per leaf row
///     without a per-row lookup (plan §8.5 slice 2: no per-row N+1 lookups).
/// </summary>
public sealed record GetActiveSessionsRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The leaves to check for an active session of the actor's own.</summary>
	public required EquatableArray<JobNodeId> LeafWorkIds { get; init; }
}
