namespace JobTrack.AdminCli;

using System.Data.Common;
using PicoArgs_dotnet;

/// <summary>
///     Resolves a connection string from either a password-free <c>--connection-string</c> or
///     <c>--connection-string-file</c> (a file's trimmed contents for credentials -- security review remediation §2.7).
///     The two are mutually exclusive; exactly one is required.
/// </summary>
internal static class ConnectionStringSource
{
	/// <summary>
	///     Reads <c>--connection-string</c>/<c>--connection-string-file</c> from <paramref name="pico" />
	///     and returns the resolved connection string, or throws <see cref="AdminCliUsageException" /> if
	///     both or neither are given.
	/// </summary>
	public static string Parse(PicoArgs pico)
	{
		ArgumentNullException.ThrowIfNull(pico);

		var direct = pico.GetParamOpt("--connection-string");
		var filePath = pico.GetParamOpt("--connection-string-file");

		if (direct is not null && filePath is not null) {
			throw new AdminCliUsageException("'--connection-string' and '--connection-string-file' are mutually exclusive.");
		}

		if (direct is not null) {
			if (ContainsPassword(direct)) {
				throw new AdminCliUsageException(
					"'--connection-string' must not contain a password; use '--connection-string-file', a PostgreSQL passfile, or integrated authentication.");
			}

			return direct;
		}

		if (filePath is not null) {
			return ReadTrimmed(filePath);
		}

		throw new AdminCliUsageException("Missing required flag '--connection-string' or '--connection-string-file'.");
	}

	private static bool ContainsPassword(string connectionString)
	{
		try {
			var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
			return builder.Keys.Cast<string>().Any(
				key => key.Equals("Password", StringComparison.OrdinalIgnoreCase) || key.Equals("Pwd", StringComparison.OrdinalIgnoreCase));
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
			throw new AdminCliUsageException($"Failed to read '--connection-string-file' '{filePath}': {ex.Message}");
		}
	}
}
