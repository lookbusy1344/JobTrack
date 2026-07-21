namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobQueries.GetActiveSessionsAsync" />. Returns every worker's unfinished
///     sessions among the given leaves under ADR 0041, mirroring <see cref="GetJobSummariesRequest" />'s
///     batch-by-ids shape — used by job-tree browsing to show the complete plural active-session state
///     per leaf without a per-row lookup (browse-sessions plan §2.4).
/// </summary>
public sealed record GetActiveSessionsRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The leaves whose active sessions should be returned.</summary>
	public required EquatableArray<JobNodeId> LeafWorkIds { get; init; }
}
