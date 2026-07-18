namespace JobTrack.Database;

/// <summary>
///     Thrown when the deployment tool refuses to proceed: an on-disk script's
///     checksum no longer matches a previously recorded application, a recorded
///     version is newer than any known script, or a script file name does not
///     match the required naming convention (ADR 0011 — fail closed).
/// </summary>
public sealed class SchemaDeploymentException : Exception
{
	public SchemaDeploymentException()
	{
	}

	public SchemaDeploymentException(string message)
		: base(message)
	{
	}

	public SchemaDeploymentException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
