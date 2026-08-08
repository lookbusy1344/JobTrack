namespace JobTrack.Persistence.Shared.Ports;

/// <summary>
///     What a database write conflict actually was, named for the constraint the driver reported
///     rather than for what any one call site makes of it (ADR 0064).
/// </summary>
/// <remarks>
///     A call site catches the kinds its own operation can provoke, in its own order of specificity,
///     exactly as the per-provider <c>Find*Violation</c> helper chains used to. Naming these after the
///     constraint rather than the interpretation is what lets one classifier serve the rate, schedule
///     and work-session ports at once: the same PostgreSQL <c>23505</c> is an overlap to a rate write
///     and "this worker is already active" to a session write.
/// </remarks>
internal enum WriteConflictKind
{
	/// <summary>Not a recognised write conflict; the exception means something else and must propagate.</summary>
	None,

	/// <summary>
	///     A closed leaf was written to: schema version 0007's
	///     <c>work_session_leaf_not_closed_on_insert/on_update</c> deferred constraint triggers (ADR 0044).
	/// </summary>
	LeafClosed,

	/// <summary>
	///     A leaf was moved to a terminal achievement while sessions were still active: schema version
	///     0007's <c>leaf_work_no_active_sessions_on_terminal</c> deferred constraint trigger (ADR 0044).
	/// </summary>
	ActiveSessions,

	/// <summary>
	///     A unique index rejected the row -- for a session write, schema version 0007's
	///     <c>work_session_one_active_per_leaf_user_idx</c> ("this worker is already active here").
	/// </summary>
	UniquenessViolation,

	/// <summary>
	///     An effective-range or session-interval overlap was rejected: PostgreSQL's GiST exclusion
	///     constraints (or the deadlock its concurrent-interleaving path raises instead), SQLite's
	///     equivalent immediate triggers.
	/// </summary>
	RangeOverlap,
}
