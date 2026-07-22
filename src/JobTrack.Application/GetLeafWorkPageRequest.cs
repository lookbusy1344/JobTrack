namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobQueries.GetLeafWorkPageAsync" />: the unified <c>/Jobs/Work</c> page's
///     single bounded read (unified-leaf-workflow plan Stage 4) -- node context, leaf version,
///     achievement, readiness, archive state, every active session, direct-dependent impact, and
///     actor-specific action capabilities, in one call regardless of session or history growth.
/// </summary>
public sealed record GetLeafWorkPageRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The leaf this page is for.</summary>
	public required JobNodeId JobNodeId { get; init; }
}
