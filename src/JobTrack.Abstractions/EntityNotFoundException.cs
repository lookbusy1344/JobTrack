namespace JobTrack.Abstractions;

/// <summary>The referenced entity does not exist, or is not visible to the caller.</summary>
public sealed class EntityNotFoundException : JobTrackException
{
	/// <summary>Creates an <see cref="EntityNotFoundException" />.</summary>
	public EntityNotFoundException()
	{
	}

	/// <summary>Creates an <see cref="EntityNotFoundException" /> with the given message.</summary>
	public EntityNotFoundException(string message)
		: base(message)
	{
	}

	/// <summary>Creates an <see cref="EntityNotFoundException" /> with the given message and inner exception.</summary>
	public EntityNotFoundException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
