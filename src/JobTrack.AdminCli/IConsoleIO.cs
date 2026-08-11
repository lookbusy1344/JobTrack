namespace JobTrack.AdminCli;

/// <summary>
///     Console input/output as an explicit dependency rather than static <see cref="Console" /> calls,
///     so command logic (<c>BootstrapCommand</c>, <c>EmergencyPasswordReset</c>) is testable without a
///     real terminal.
/// </summary>
public interface IConsoleIO
{
	void WriteLine(string message);

	void WriteError(string message);

	string ReadLine(string prompt);

	/// <summary>Prompts for and reads a line without echoing the typed characters.</summary>
	string ReadPassword(string prompt);

	/// <summary>
	///     Reads one line from standard input with no prompt and no echo suppression -- for
	///     <c>--password-stdin</c> (security review remediation §2.7), where the caller has already
	///     redirected/piped a secret in (e.g.
	///     <c>
	///         echo "$PASSWORD" | JobTrack.AdminCli bootstrap ...
	///         --password-stdin
	///     </c>
	///     ), matching the <c>docker login --password-stdin</c> convention.
	/// </summary>
	string ReadStdinLine();
}
