namespace JobTrack.Domain.Hierarchy;

using Abstractions;

/// <summary>
///     The canonical achievement state machine (ADR 0001):
///     <c>
///         Waiting -&gt; InProgress -&gt;
///         {Success, Cancelled, Unsuccessful}
///     </c>
///     , <c>Waiting -&gt; {Cancelled, Unsuccessful}</c> directly,
///     and any terminal state may be reopened back to <c>Waiting</c>.
/// </summary>
public static class AchievementTransitions
{
	/// <summary>Whether <paramref name="from" /> may transition directly to <paramref name="to" />.</summary>
	public static bool IsPermitted(Achievement from, Achievement to) => from switch {
		Achievement.None => false,
		Achievement.Waiting => to is Achievement.InProgress or Achievement.Cancelled or Achievement.Unsuccessful,
		Achievement.InProgress => to is Achievement.Success or Achievement.Cancelled or Achievement.Unsuccessful,
		Achievement.Success => to == Achievement.Waiting,
		Achievement.Cancelled => to == Achievement.Waiting,
		Achievement.Unsuccessful => to == Achievement.Waiting,
		_ => throw new ArgumentOutOfRangeException(nameof(from), from, "Unrecognized achievement value."),
	};

	/// <summary>
	///     Whether <paramref name="achievement" /> is one of the three terminal states (spec §6: a
	///     dependent <c>LeafWork</c> cannot transition into any of these while its prerequisites are
	///     unsatisfied).
	/// </summary>
	public static bool IsCompletedState(Achievement achievement) => achievement switch {
		Achievement.None => false,
		Achievement.Waiting => false,
		Achievement.InProgress => false,
		Achievement.Success => true,
		Achievement.Cancelled => true,
		Achievement.Unsuccessful => true,
		_ => throw new ArgumentOutOfRangeException(nameof(achievement), achievement, "Unrecognized achievement value."),
	};

	/// <summary>
	///     Whether this transition reopens a terminal state back to <see cref="Achievement.Waiting" />
	///     (ADR 0001), which requires elevated authorization regardless of node ownership.
	/// </summary>
	public static bool IsReopening(Achievement from, Achievement to) => to == Achievement.Waiting && IsCompletedState(from);
}
