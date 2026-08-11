namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobQueries.GetJobNodeAsync" />. Carries no ownership-based authorization
///     gate, matching <see cref="GetReadinessRequest" /> — viewing job data is an unqualified baseline
///     capability for every role (spec §7.3).
/// </summary>
public sealed record GetJobNodeRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The node to retrieve, or <see langword="null" /> for the permanent root.</summary>
	public JobNodeId? NodeId { get; init; }
}
