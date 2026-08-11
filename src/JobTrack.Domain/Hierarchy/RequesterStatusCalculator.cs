namespace JobTrack.Domain.Hierarchy;

using Abstractions;

/// <summary>
///     Derives the public <see cref="RequesterStatus" /> vocabulary (ADR 0034, plan §7) from a request's
///     acknowledgement flag and the achievement of every childless node in its subtree — the same
///     childless-node unit <c>node_succeeded</c> (schema version 0013) and <see cref="AchievementCalculator" />
///     evaluate. Progress is computed from the whole subtree, not only the anchor node, so that
///     decomposition into sub-jobs is reflected.
/// </summary>
public static class RequesterStatusCalculator
{
	/// <summary>
	///     Precedence, most-decided first: every childless node succeeded → <see cref="RequesterStatus.Completed" />;
	///     every childless node with recorded work reached a terminal, non-success outcome and none is
	///     still pending → <see cref="RequesterStatus.Cancelled" />; any childless node is
	///     <see cref="Achievement.InProgress" /> → <see cref="RequesterStatus.InProgress" />; any childless
	///     node is <see cref="Achievement.Waiting" /> → <see cref="RequesterStatus.Waiting" />;
	///     <paramref name="acknowledged" /> → <see cref="RequesterStatus.Accepted" />; otherwise
	///     <see cref="RequesterStatus.Submitted" />.
	/// </summary>
	public static RequesterStatus Derive(bool acknowledged, IReadOnlyCollection<RequesterSubtreeLeafState> subtreeLeaves)
	{
		ArgumentNullException.ThrowIfNull(subtreeLeaves);

		if (subtreeLeaves.Count > 0 && subtreeLeaves.All(leaf => leaf.LeafAchievement == Achievement.Success)) {
			return RequesterStatus.Completed;
		}

		if (subtreeLeaves.Count > 0 &&
			subtreeLeaves.All(leaf => leaf.LeafAchievement is not null && IsTerminalNegative(leaf.LeafAchievement.Value))) {
			return RequesterStatus.Cancelled;
		}

		if (subtreeLeaves.Any(leaf => leaf.LeafAchievement == Achievement.InProgress)) {
			return RequesterStatus.InProgress;
		}

		if (subtreeLeaves.Any(leaf => leaf.LeafAchievement == Achievement.Waiting)) {
			return RequesterStatus.Waiting;
		}

		return acknowledged ? RequesterStatus.Accepted : RequesterStatus.Submitted;
	}

	private static bool IsTerminalNegative(Achievement achievement) => achievement switch {
		Achievement.None => false,
		Achievement.Waiting => false,
		Achievement.InProgress => false,
		Achievement.Success => false,
		Achievement.Cancelled => true,
		Achievement.Unsuccessful => true,
		_ => throw new ArgumentOutOfRangeException(nameof(achievement), achievement, "Unrecognized achievement value."),
	};
}
