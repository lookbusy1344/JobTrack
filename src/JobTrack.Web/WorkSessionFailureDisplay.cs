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
			ConstraintIds.WorkSessionAlreadyActive => "This worker already has an active session for this leaf.",
			ConstraintIds.WorkSessionStartInFuture or ConstraintIds.WorkSessionFinishInFuture => "That time is in the future — enter a past time.",
			ConstraintIds.WorkSessionOverlap => "That time overlaps another session for this leaf.",
			ConstraintIds.WorkSessionInvalidInterval => "The finish time must be after the start time.",
			ConstraintIds.WorkSessionLeafClosed =>
				"This leaf is closed to new sessions. Reopen it and/or restore it before starting another session.",
			ConstraintIds.LeafClosureActiveSessions => "This leaf cannot be closed while a session is still active. Finish it first.",
			ConstraintIds.WorkSessionTargetNotEligible => "That worker is disabled or no longer eligible. Choose another worker.",
			_ => exception.Message,
		};
	}
}
