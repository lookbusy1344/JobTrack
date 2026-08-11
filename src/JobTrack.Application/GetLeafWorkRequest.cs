namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="IJobQueries.GetLeafWorkAsync" />. Carries no ownership-based authorization
///     gate, matching <see cref="GetReadinessRequest" /> — viewing job data, including a leaf's
///     achievement state, is an unqualified baseline capability for every role (spec §7.3).
/// </summary>
public sealed record GetLeafWorkRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The leaf whose <c>LeafWork</c> is requested.</summary>
	public required JobNodeId JobNodeId { get; init; }
}
