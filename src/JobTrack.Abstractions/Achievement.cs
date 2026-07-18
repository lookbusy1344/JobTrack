namespace JobTrack.Abstractions;

/// <summary>
///     Canonical achievement states (ADR 0001). Only <see cref="Success" /> satisfies a prerequisite;
///     <see cref="Cancelled" /> and <see cref="Unsuccessful" /> are both terminal, non-success outcomes.
///     Values match the seeded <c>achievement_status</c> reference-table ids exactly (database schema
///     version 0001) so persistence mapping never needs a separate translation table.
/// </summary>
public enum Achievement
{
	/// <summary>No achievement has been recorded yet.</summary>
	None = 0,

	/// <summary>No work has started; the initial state.</summary>
	Waiting = 1,

	/// <summary>Work is under way.</summary>
	InProgress = 2,

	/// <summary>Completed successfully. The only state that satisfies a prerequisite.</summary>
	Success = 3,

	/// <summary>Terminal: withdrawn without being attempted to completion.</summary>
	Cancelled = 4,

	/// <summary>Terminal: attempted but did not succeed.</summary>
	Unsuccessful = 5,
}
