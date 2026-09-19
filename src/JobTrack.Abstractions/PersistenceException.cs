namespace JobTrack.Abstractions;

/// <summary>
///     The configured persistence provider could not complete an operation for a reason that is not a
///     caller-visible domain condition. This stable library exception prevents provider-specific
///     exceptions from crossing the public <c>IJobTrackClient</c> boundary (spec §13.2).
/// </summary>
public sealed class PersistenceException : JobTrackException
{
	private const string DefaultMessage = "The database could not complete the operation.";

	/// <summary>Creates a <see cref="PersistenceException" />.</summary>
	public PersistenceException()
		: base(DefaultMessage) { }

	/// <summary>Creates a <see cref="PersistenceException" /> wrapping the provider failure.</summary>
	public PersistenceException(Exception innerException)
		: base(DefaultMessage, innerException) { }

	/// <summary>Creates a <see cref="PersistenceException" /> with the given message and inner exception.</summary>
	public PersistenceException(string message, Exception innerException)
		: base(message, innerException) { }
}
