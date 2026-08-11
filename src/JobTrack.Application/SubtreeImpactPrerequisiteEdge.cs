namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     A prerequisite edge joining a <see cref="SubtreeImpactResult" />'s subtree to a node outside it.
///     ADR 0061 drops such edges with the subtree instead of refusing the deletion.
/// </summary>
public sealed record SubtreeImpactPrerequisiteEdge
{
	/// <summary>The node that must finish first.</summary>
	public required JobNodeId FromId { get; init; }

	/// <summary>The node that waits on <see cref="FromId" />.</summary>
	public required JobNodeId ToId { get; init; }

	/// <summary>The outside node's description — the one whose readiness changes when the edge is dropped.</summary>
	public required string ExternalDescription { get; init; }

	/// <summary>
	///     Whether the node outside the subtree is the one that *waits*. When true, dropping this edge
	///     can unblock it; when false, the outside node was merely a prerequisite of doomed work.
	/// </summary>
	public required bool ExternalNodeIsDependent { get; init; }
}
