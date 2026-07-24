namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     One coherent persistence snapshot for a bounded subtree: the rows to render and the requested
///     root's recursively derived achievement when that root is a branch or the permanent root.
/// </summary>
internal sealed record JobSubtreeQueryResult
{
	/// <summary>The bounded subtree rows.</summary>
	public required EquatableArray<JobNodeSubtreeRow> Rows { get; init; }

	/// <summary>
	///     The root's derived achievement, or <see langword="null" /> when the requested root is a leaf.
	/// </summary>
	public BranchAchievement? RootAchievement { get; init; }
}
