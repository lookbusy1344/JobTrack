namespace JobTrack.AdminCli;

using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Identity;

/// <summary>
///     The <c>bootstrap</c> command (plan §8.6): a thin wrapper collecting protected interactive input
///     and invoking <see cref="IInstallationCommands.BootstrapAdministratorAsync" /> — it does not
///     reimplement the transaction. Bootstrap semantics are already covered in depth by
///     <c>InstallationCommandsTests</c> and <c>ProviderIntegrationTests</c>; this type's own
///     responsibility is prompting, never echoing the password back, and reporting the result.
/// </summary>
public static class BootstrapCommand
{
	/// <summary>
	///     Default IANA time zone offered for the administrator prompt, per this
	///     deployment's UK-standard defaulting convention.
	/// </summary>
	private const string DefaultIanaTimeZone = "Europe/London";

	private const decimal DefaultHourlyRateAmount = 20m;

	public static async Task<int> RunAsync(
		IConsoleIO io,
		IInstallationCommands installationCommands,
		string defaultUserName,
		CancellationToken cancellationToken,
		string? password = null,
		UserManager<JobTrackIdentityUser>? userManager = null,
		bool forcePasswordChange = true)
	{
		ArgumentNullException.ThrowIfNull(io);
		ArgumentNullException.ThrowIfNull(installationCommands);
		ArgumentNullException.ThrowIfNull(defaultUserName);
		if (!forcePasswordChange && userManager is null) {
			throw new ArgumentNullException(nameof(userManager), "Clearing the forced password change requires a UserManager.");
		}

		var displayName = io.ReadLine("Administrator display name: ");
		var ianaTimeZone = ReadLineWithDefault(io, "IANA time zone", DefaultIanaTimeZone);
		var userName = ReadLineWithDefault(io, "Username", defaultUserName);
		password ??= ReadConfirmedPassword(io);

		try {
			var result = await installationCommands.BootstrapAdministratorAsync(
				new() {
					DisplayName = displayName,
					IanaTimeZone = ianaTimeZone,
					DefaultHourlyRate = new HourlyRate(DefaultHourlyRateAmount),
					UserName = userName,
					Password = password,
					CorrelationId = Guid.NewGuid(),
				},
				cancellationToken).ConfigureAwait(false);

			if (!forcePasswordChange && !await ForcedPasswordChange.ClearAsync(io, userManager!, userName).ConfigureAwait(false)) {
				return 1;
			}

			io.WriteLine(
				$"Bootstrap complete. Administrator id {result.AdministratorId.Value}, root job node id {result.RootJobNodeId.Value}.");
			return 0;
		}
		catch (InvariantViolationException ex) {
			io.WriteError($"Bootstrap failed: {ex.Message}");
			return 1;
		}
	}

	private static string ReadLineWithDefault(IConsoleIO io, string label, string defaultValue)
	{
		var input = io.ReadLine($"{label} [{defaultValue}]: ");
		return string.IsNullOrWhiteSpace(input) ? defaultValue : input;
	}

	private static string ReadConfirmedPassword(IConsoleIO io)
	{
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
