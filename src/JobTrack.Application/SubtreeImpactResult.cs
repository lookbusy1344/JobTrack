namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     What a <see cref="IJobCommands.DeleteSubtreeAsync" /> rooted at
///     <see cref="SubtreeImpactRequest.RootId" /> would destroy (ADR 0061). Counts are exact over the
///     whole subtree — this is deliberately not built on the Browse subtree query, whose ADR 0039 depth
///     and breadth caps would silently under-report.
/// </summary>
public sealed record SubtreeImpactResult
{
	/// <summary>The subtree root that was measured.</summary>
	public required JobNodeId RootId { get; init; }

	/// <summary>Every node in the subtree, root first, then descendants in depth order.</summary>
	public required EquatableArray<SubtreeImpactNode> Nodes { get; init; }

	/// <summary>Total <c>job_node</c> rows that would be deleted, including the root.</summary>
	public required int NodeCount { get; init; }

	/// <summary>How many of those nodes have an attached <c>leaf_work</c> row.</summary>
	public required int LeafWorkCount { get; init; }

	/// <summary>Total <c>work_session</c> rows that would be destroyed.</summary>
	public required int WorkSessionCount { get; init; }

	/// <summary>Total recorded work across every destroyed session — the history that stops counting toward ancestor cost.</summary>
	public required Duration TotalWorkedDuration { get; init; }

	/// <summary>Prerequisite edges with both endpoints inside the subtree; dropped with it, affecting nothing outside.</summary>
	public required int InternalPrerequisiteEdgeCount { get; init; }

	/// <summary>
	///     Prerequisite edges joining the subtree to a node outside it. ADR 0061 drops these rather than
	///     refusing, so an external dependent can become ready where it was blocked — the confirmation
	///     screen names them for that reason.
	/// </summary>
	public required EquatableArray<SubtreeImpactPrerequisiteEdge> ExternalPrerequisiteEdges { get; init; }

	/// <summary>Requester <c>job_request</c> rows (and their notes) that would be destroyed with their nodes.</summary>
	public required int JobRequestCount { get; init; }

	/// <summary>
	///     Request holding areas anchored at a node in the subtree. Non-empty means
	///     <see cref="IJobCommands.DeleteSubtreeAsync" /> will refuse: a holding area is a department's
	///     intake configuration, not this subtree's data (ADR 0061).
	/// </summary>
	public required EquatableArray<SubtreeImpactHoldingArea> BlockingHoldingAreas { get; init; }

	/// <summary>Whether the measured root is the permanent root, which is never deletable (ADR 0015).</summary>
	public required bool IsPermanentRoot { get; init; }

	/// <summary>Whether deletion is possible at all — no anchored holding area, and the root is not the permanent root.</summary>
	public bool CanDelete => BlockingHoldingAreas.Count == 0 && !IsPermanentRoot;
}
