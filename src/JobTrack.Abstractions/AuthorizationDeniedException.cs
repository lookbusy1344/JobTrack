namespace JobTrack.Abstractions;

/// <summary>
///     The caller is authenticated but not authorized for the requested domain scope or ownership
///     (spec §13.2: the library authorizes using authoritative stored data, not caller-supplied role
///     claims alone).
/// </summary>
public sealed class AuthorizationDeniedException : JobTrackException
{
	/// <summary>Creates an <see cref="AuthorizationDeniedException" />.</summary>
	public AuthorizationDeniedException()
	{
	}

	/// <summary>Creates an <see cref="AuthorizationDeniedException" /> with the given message.</summary>
	public AuthorizationDeniedException(string message)
		: base(message)
	{
	}

	/// <summary>Creates an <see cref="AuthorizationDeniedException" /> with the given message and inner exception.</summary>
	public AuthorizationDeniedException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
