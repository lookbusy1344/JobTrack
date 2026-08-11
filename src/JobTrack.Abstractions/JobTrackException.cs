namespace JobTrack.Abstractions;

/// <summary>
///     Base of the shallow public exception hierarchy for conditions a caller handles distinctly from
///     an ordinary usage error (plan §7.1, ADR 0019): not found, authorization denied, concurrency
///     conflict, prerequisite blocked, missing rate, and invariant violation. A caller argument mistake
///     throws a framework exception (<see cref="ArgumentException" /> and siblings) directly instead of
///     a <see cref="JobTrackException" /> subtype — this hierarchy exists only for conditions that are
///     not simply "the caller passed a bad argument".
/// </summary>
public abstract class JobTrackException : Exception
{
	/// <summary>Creates a <see cref="JobTrackException" />.</summary>
	protected JobTrackException()
	{
	}

	/// <summary>Creates a <see cref="JobTrackException" /> with the given message.</summary>
	protected JobTrackException(string message)
		: base(message)
	{
	}

	/// <summary>Creates a <see cref="JobTrackException" /> with the given message and inner exception.</summary>
	protected JobTrackException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
