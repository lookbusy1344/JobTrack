namespace JobTrack.Abstractions;

/// <summary>
///     A mutation's supplied <c>version</c> did not match the current row — a zero-row
///     compare-and-swap update (plan §7.4). The caller must re-read the current state and retry with
///     the new version rather than assume the write happened.
/// </summary>
public sealed class ConcurrencyConflictException : JobTrackException
{
	/// <summary>Creates a <see cref="ConcurrencyConflictException" />.</summary>
	public ConcurrencyConflictException()
	{
	}

	/// <summary>Creates a <see cref="ConcurrencyConflictException" /> with the given message.</summary>
	public ConcurrencyConflictException(string message)
		: base(message)
	{
	}

	/// <summary>Creates a <see cref="ConcurrencyConflictException" /> with the given message and inner exception.</summary>
	public ConcurrencyConflictException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
