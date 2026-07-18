namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobQueries.GetJobSummariesAsync" />. Carries no ownership-based
///     authorization gate (see <see cref="GetJobNodeRequest" />) — this is an opportunistic
///     describe-what-you-can lookup, not a single required-entity fetch (see
///     <see cref="Ports.IJobBrowseQueryPort.GetSummariesByIdsAsync" />).
/// </summary>
public sealed record GetJobSummariesRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The nodes to describe. An id that no longer resolves is silently omitted from the result.</summary>
	public required EquatableArray<JobNodeId> NodeIds { get; init; }
}
