namespace JobTrack.Database;

using System.Data.Common;

/// <summary>
///     Resolves a connection string from either a password-free <c>--connection-string</c> or
///     <c>--connection-string-file</c> (a file's trimmed contents for credentials -- security review remediation §2.7).
///     The two are mutually exclusive; exactly one is required.
/// </summary>
public static class ConnectionStringSource
{
	/// <summary>
	///     Returns the resolved connection string, or throws <see cref="SchemaDeploymentException" /> if
	///     both or neither of <paramref name="direct" />/<paramref name="filePath" /> are given.
	/// </summary>
	public static string Resolve(string? direct, string? filePath)
	{
		if (direct is not null && filePath is not null) {
			throw new SchemaDeploymentException("'--connection-string' and '--connection-string-file' are mutually exclusive.");
		}

		if (direct is not null) {
			if (ContainsPassword(direct)) {
				throw new SchemaDeploymentException(
					"'--connection-string' must not contain a password; use '--connection-string-file', a PostgreSQL passfile, or integrated authentication.");
			}

			return direct;
		}

		if (filePath is not null) {
			return ReadTrimmed(filePath);
		}

		throw new SchemaDeploymentException("Missing required flag '--connection-string' or '--connection-string-file'.");
	}

	private static bool ContainsPassword(string connectionString)
	{
		try {
			var builder = new DbConnectionStringBuilder {
				ConnectionString = connectionString,
			};
			return builder.Keys.Cast<string>().Any(key =>
				key.Equals("Password", StringComparison.OrdinalIgnoreCase) || key.Equals("Pwd", StringComparison.OrdinalIgnoreCase));
		}
		catch (ArgumentException) {
			return false;
		}
	}

	private static string ReadTrimmed(string filePath)
	{
		try {
			return File.ReadAllText(filePath).Trim();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			throw new SchemaDeploymentException($"Failed to read '--connection-string-file' '{filePath}': {ex.Message}");
		}
	}
}
