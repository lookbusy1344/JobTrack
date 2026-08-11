namespace JobTrack.Abstractions;

/// <summary>
///     A persisted row's IANA time zone id is no longer recognized by the current TZDB (a rename or
///     merge retired the alias after the row was written). This is a server-state fault, not a caller
///     usage error -- the caller's request was valid; the stored data has rotted -- so it is kept
///     distinct from the write-path validation failure that throws the framework's own time-zone
///     lookup exception directly.
/// </summary>
public sealed class UnknownStoredTimeZoneException : JobTrackException
{
	/// <summary>Creates an <see cref="UnknownStoredTimeZoneException" />.</summary>
	public UnknownStoredTimeZoneException()
	{
	}

	/// <summary>Creates an <see cref="UnknownStoredTimeZoneException" /> with the given message.</summary>
	public UnknownStoredTimeZoneException(string message)
		: base(message)
	{
	}

	/// <summary>Creates an <see cref="UnknownStoredTimeZoneException" /> with the given message and inner exception.</summary>
	public UnknownStoredTimeZoneException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
