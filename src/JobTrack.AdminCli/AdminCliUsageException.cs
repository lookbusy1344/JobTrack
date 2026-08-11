namespace JobTrack.AdminCli;

/// <summary>
///     Malformed command-line input to <c>JobTrack.AdminCli</c> — mirrors
///     <c>JobTrack.Database</c>'s <c>SchemaDeploymentException</c>'s role for its own CLI.
/// </summary>
public sealed class AdminCliUsageException : Exception
{
	public AdminCliUsageException()
	{
	}

	public AdminCliUsageException(string message)
		: base(message)
	{
	}

	public AdminCliUsageException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
