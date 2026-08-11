namespace JobTrack.Web;

using Abstractions;

/// <summary>
///     The <c>_BranchAchievementIcon</c> partial's model: a branch's (or the root's) rollup
///     achievement, collapsed to <see cref="BranchAchievement" />'s two states rather than a leaf's
///     six -- the branch counterpart to <see cref="AchievementIconModel" />.
/// </summary>
public sealed class BranchAchievementIconModel
{
	/// <summary>The rollup achievement to draw.</summary>
	public required BranchAchievement Achievement { get; init; }

	/// <summary>
	///     Whether the achievement's name renders as visible text beside the glyph — see
	///     <see cref="AchievementIconModel.ShowLabel" />.
	/// </summary>
	public bool ShowLabel { get; init; } = true;
}
