namespace JobTrack.Abstractions;

/// <summary>
///     An operation that requires a satisfied prerequisite (spec §6) was attempted while the
///     prerequisite is unsatisfied — starting or completing work, not finishing an already-open
///     session, which remains possible after prerequisite regression.
/// </summary>
public sealed class PrerequisiteBlockedException : JobTrackException
{
	/// <summary>Creates a <see cref="PrerequisiteBlockedException" />.</summary>
	public PrerequisiteBlockedException()
	{
	}

	/// <summary>Creates a <see cref="PrerequisiteBlockedException" /> with the given message.</summary>
	public PrerequisiteBlockedException(string message)
		: base(message)
	{
	}

	/// <summary>Creates a <see cref="PrerequisiteBlockedException" /> with the given message and inner exception.</summary>
	public PrerequisiteBlockedException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
