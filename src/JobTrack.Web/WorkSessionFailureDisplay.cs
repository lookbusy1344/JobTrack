namespace JobTrack.Web;

using Abstractions;

/// <summary>
///     Turns a start/finish <see cref="InvariantViolationException" /> into the sentence a worker
///     reads. Shared by every page offering those actions so the same rejected backdate reads the
///     same way whether it came from Browse, the awaiting-progress dashboard, or a leaf's own page.
///     Unrecognized constraints fall through to the exception's own message rather than a generic
///     apology — a new invariant should surface, not be swallowed.
/// </summary>
public static class WorkSessionFailureDisplay
{
	public static string Describe(InvariantViolationException exception)
	{
		ArgumentNullException.ThrowIfNull(exception);

		return exception.ConstraintId switch {
			"work-session-already-active" => "This worker already has an active session for this leaf.",
			"work-session-start-in-future" or "work-session-finish-in-future" => "That time is in the future — enter a past time.",
			"work-session-overlap" => "That time overlaps another session for this leaf.",
			"work-session-invalid-interval" => "The finish time must be after the start time.",
			_ => exception.Message,
		};
	}
}
