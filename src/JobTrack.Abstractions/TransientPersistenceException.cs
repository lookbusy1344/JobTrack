namespace JobTrack.Abstractions;

/// <summary>
///     A write could not complete because of a transient database condition — a deadlock, a
///     serialization failure, or a busy database (2.2 of the 2026-09-18 fresh-eyes remediation) — not
///     because the caller's request violated an invariant. The same request may succeed if retried; the
///     hosts surface it as a retryable failure (HTTP 503), never as the caller's own mistake.
/// </summary>
public sealed class TransientPersistenceException : JobTrackException
{
	private const string DefaultMessage = "The database could not complete this write; retry.";

	/// <summary>Creates a <see cref="TransientPersistenceException" />.</summary>
	public TransientPersistenceException()
		: base(DefaultMessage) { }

	/// <summary>Creates a <see cref="TransientPersistenceException" /> wrapping the transient provider failure.</summary>
	public TransientPersistenceException(Exception innerException)
		: base(DefaultMessage, innerException) { }

	/// <summary>Creates a <see cref="TransientPersistenceException" /> with the given message and inner exception.</summary>
	public TransientPersistenceException(string message, Exception innerException)
		: base(message, innerException) { }
}
