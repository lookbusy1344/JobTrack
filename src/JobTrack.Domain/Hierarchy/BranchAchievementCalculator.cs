namespace JobTrack.Domain.Hierarchy;

using Abstractions;

/// <summary>
///     Reduces spec §5.2's recursive achievement rule to the two-value <see cref="BranchAchievement" />
///     rollup a branch (or the root) carries, given the achievement of every childless node in its
///     subtree — the same set-based equivalent the PostgreSQL <c>node_succeeded</c> function evaluates
///     (schema version 0013), for callers that already hold those childless-node states and would
///     otherwise re-query for them. <see cref="AchievementCalculator" /> remains the form to use when
///     the full <see cref="HierarchyNode" /> graph is at hand.
/// </summary>
public static class BranchAchievementCalculator
{
	/// <summary>
	///     <see cref="BranchAchievement.Success" /> iff <paramref name="subtreeLeaves" /> is non-empty and
	///     every childless node in it holds <see cref="Achievement.Success" />; otherwise
	///     <see cref="BranchAchievement.Unfinished" />. A childless node with no <c>LeafWork</c> row
	///     (<see cref="RequesterSubtreeLeafState.LeafAchievement" /> <see langword="null" />) never
	///     succeeds, matching <c>node_succeeded</c>.
	/// </summary>
	public static BranchAchievement Derive(IReadOnlyCollection<RequesterSubtreeLeafState> subtreeLeaves)
	{
		ArgumentNullException.ThrowIfNull(subtreeLeaves);

		return subtreeLeaves.Count > 0 && subtreeLeaves.All(leaf => leaf.LeafAchievement == Achievement.Success)
			? BranchAchievement.Success
			: BranchAchievement.Unfinished;
	}
}
