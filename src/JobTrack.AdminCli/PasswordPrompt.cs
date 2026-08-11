namespace JobTrack.AdminCli;

/// <summary>
///     Shared masked, confirmed interactive password prompt (security review remediation §2.7) --
///     used by both <see cref="BootstrapCommand" /> and <c>create-employee</c>'s effective-password
///     resolution in <see cref="Program" />, so a caller who does not pass <c>--password-stdin</c>
///     never has to type a production credential in a way the terminal echoes
///     or that lands in shell history.
/// </summary>
internal static class PasswordPrompt
{
	public static string ReadConfirmed(IConsoleIO io)
	{
		ArgumentNullException.ThrowIfNull(io);

		while (true) {
			var password = io.ReadPassword("Password: ");
			var confirmation = io.ReadPassword("Confirm password: ");

			if (password == confirmation) {
				return password;
			}

			io.WriteError("Passwords did not match; try again.");
		}
	}
}
