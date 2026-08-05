namespace JobTrack.Application;

/// <summary>What <see cref="IJobCommands.ArchiveSubtreeAsync" /> changed (ADR 0061).</summary>
public sealed record ArchiveSubtreeResult
{
	/// <summary><c>job_node</c> rows in the subtree, including the root and those already archived.</summary>
	public required int NodeCount { get; init; }

	/// <summary>How many of those were newly archived; nodes already archived keep their original instant.</summary>
	public required int NewlyArchivedCount { get; init; }
}
