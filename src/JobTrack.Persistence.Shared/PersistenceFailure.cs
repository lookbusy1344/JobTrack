namespace JobTrack.Persistence.Shared;

/// <summary>
///     What a database write failure means for the caller (2.2 of the 2026-09-18 fresh-eyes
///     remediation): a broken invariant the caller can be told about, a transient condition worth a
///     retry, or something unknown that must reach the exception handler unwrapped as the fault it is.
/// </summary>
internal enum PersistenceFailure
{
	/// <summary>An integrity constraint was violated (SQLSTATE class 23, the project's P00xx trigger codes, or a SQLite constraint) -- a genuine invariant.</summary>
	Integrity,

	/// <summary>A transient rollback (SQLSTATE class 40 -- deadlock/serialization -- or a busy/locked SQLite database): the same write may succeed on retry.</summary>
	Transient,

	/// <summary>
	///     Neither -- a dropped connection, a timeout, resource exhaustion, or any other failure that is a fault, not the caller's mistake, and must
	///     propagate unwrapped.
	/// </summary>
	Unknown,
}
