namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     One node created by <see cref="IJobCommands.ImportSubtreeAsync" />, mapping the caller's
///     request-local id back to the real <c>job_node</c> identifier it was created as.
/// </summary>
public sealed record ImportedJobNode
{
	/// <summary>The request-local identifier from the originating <see cref="ImportSubtreeNodeSpec" />.</summary>
	public required long LocalId { get; init; }

	/// <summary>The <c>job_node</c> identifier this node was created as.</summary>
	public required JobNodeId JobNodeId { get; init; }
}
