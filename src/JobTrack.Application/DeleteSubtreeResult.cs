namespace JobTrack.Application;

using NodaTime;

/// <summary>
///     What <see cref="IJobCommands.DeleteSubtreeAsync" /> actually destroyed (ADR 0061), measured
///     inside its own transaction rather than taken from the caller's earlier
///     <see cref="SubtreeImpactResult" />, so it reflects the subtree as it was at the moment of
///     deletion.
/// </summary>
public sealed record DeleteSubtreeResult
{
	/// <summary><c>job_node</c> rows deleted, including the subtree root.</summary>
	public required int NodeCount { get; init; }

	/// <summary><c>leaf_work</c> rows deleted.</summary>
	public required int LeafWorkCount { get; init; }

	/// <summary><c>work_session</c> rows deleted.</summary>
	public required int WorkSessionCount { get; init; }

	/// <summary>Total recorded work destroyed — the history that no longer counts toward ancestor cost.</summary>
	public required Duration TotalWorkedDuration { get; init; }

	/// <summary>Prerequisite edges deleted, both internal to the subtree and crossing its boundary.</summary>
	public required int PrerequisiteEdgeCount { get; init; }

	/// <summary>Requester <c>job_request</c> rows deleted along with their nodes.</summary>
	public required int JobRequestCount { get; init; }
}
