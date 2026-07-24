namespace JobTrack.Abstractions;

/// <summary>
///     A branch's (or the root's) rollup achievement, derived at read time from every leaf in its
///     subtree, never stored: <see cref="Success" /> iff every leaf has succeeded, recursively through
///     any nested branches, <see cref="Unfinished" /> otherwise. Deliberately collapses the six-value
///     <see cref="Achievement" /> vocabulary a single leaf carries down to these two states — a branch
///     does not itself hold work, so finer leaf-only distinctions (waiting vs. in progress vs.
///     cancelled) don't apply to it.
/// </summary>
public enum BranchAchievement
{
	/// <summary>At least one leaf in the subtree has not succeeded.</summary>
	Unfinished = 0,

	/// <summary>Every leaf in the subtree has succeeded.</summary>
	Success = 1,
}
