namespace JobTrack.Web;

using Abstractions;

/// <summary>
///     The <c>_AchievementIcon</c> partial's model: a leaf's recorded achievement drawn as one of the
///     sign family, wherever a job appears. The state counterpart to <see cref="KindIconModel" />, and
///     deliberately separate from readiness — a job carries a blocked marker *alongside* its
///     achievement, never instead of it.
/// </summary>
public sealed class AchievementIconModel
{
	/// <summary>
	///     The achievement to draw. <see langword="null" /> when no <c>leaf_work</c> is attached — a
	///     branch, or a leaf nobody has attached work to yet — in which case the partial renders
	///     nothing at all rather than inventing a glyph for the absence of a state.
	/// </summary>
	public required Achievement? Achievement { get; init; }

	/// <summary>
	///     Whether the achievement's name renders as visible text beside the glyph (a detail panel, a
	///     column wide enough for it) or stays visually-hidden only (a tree row already carrying a
	///     name, a kind glyph, and actions) — either way the glyph itself is <c>aria-hidden</c>, so
	///     the state reaches assistive tech through this text and never through colour or shape alone.
	/// </summary>
	public bool ShowLabel { get; init; } = true;
}
