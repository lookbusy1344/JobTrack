namespace JobTrack.Abstractions;

/// <summary>
///     A cost calculation reached a session or segment with no resolvable rate under the precedence
///     chain (exception rate, node override, user rate, default rate — spec §9), so no eligible rate
///     applies and no amount can be computed.
/// </summary>
public sealed class MissingRateException : JobTrackException
{
	/// <summary>Creates a <see cref="MissingRateException" />.</summary>
	public MissingRateException()
	{
	}

	/// <summary>Creates a <see cref="MissingRateException" /> with the given message.</summary>
	public MissingRateException(string message)
		: base(message)
	{
	}

	/// <summary>Creates a <see cref="MissingRateException" /> with the given message and inner exception.</summary>
	public MissingRateException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
