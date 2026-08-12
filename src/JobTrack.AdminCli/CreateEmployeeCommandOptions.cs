namespace JobTrack.AdminCli;

using System.Globalization;
using Abstractions;
using PicoArgs_dotnet;

/// <summary>Parsed arguments for the <c>create-employee</c> CLI command.</summary>
public sealed record CreateEmployeeCommandOptions
{
	/// <summary>
	///     IANA time zone offered when <c>--iana-time-zone</c> is omitted, per this
	///     deployment's UK-standard defaulting convention (matches <see cref="BootstrapCommand" />).
	/// </summary>
	private const string DefaultIanaTimeZone = "Europe/London";

	public required AdminCliProvider Provider { get; init; }

	public required string ConnectionString { get; init; }

	/// <summary>
	///     The administrator performing the creation. <c>CreateEmployeeAsync</c> is
	///     administrator-only (spec §7.1), so this names an existing administrator whose id becomes the
	///     command's actor.
	/// </summary>
	public required string ActorUsername { get; init; }

	public required string Username { get; init; }

	/// <summary>
	///     The resolved new-account credential. Argument parsing always leaves this null; the host
	///     resolves it from standard input or the masked interactive prompt before invoking the command.
	/// </summary>
	public string? Password { get; init; }

	/// <summary>
	///     <see langword="true" /> when <c>--password-stdin</c> is passed: the new account's initial
	///     credential is read as one line from standard input, matching
	///     <see cref="BootstrapCommandOptions.PasswordFromStdin" />.
	/// </summary>
	public bool PasswordFromStdin { get; init; }

	public required string DisplayName { get; init; }

	public required string IanaTimeZone { get; init; }

	/// <summary>
	///     The roles to grant, in order; the first is the account's initial role
	///     (<c>CreateEmployeeAsync</c>, ADR 0023) and any remainder are granted afterwards
	///     (<c>AssignRoleAsync</c>). Never empty.
	/// </summary>
	public required EquatableArray<EmployeeRole> Roles { get; init; }

	public decimal? DefaultHourlyRate { get; init; }

	/// <summary>
	///     <see langword="false" /> when <c>--no-force-password-change</c> is passed, clearing
	///     the ADR 0023 default so a published/shared demo credential remains usable without a forced
	///     change on first sign-in. <see langword="true" /> otherwise (the normal secure default).
	/// </summary>
	public required bool ForcePasswordChange { get; init; }

	/// <summary>
	///     Reads the command's flags from <paramref name="pico" /> and calls
	///     <see cref="PicoArgs.Finished" /> — the caller has already consumed the leading command via
	///     <see cref="PicoArgs.GetCommand" />.
	/// </summary>
	public static CreateEmployeeCommandOptions Parse(PicoArgs pico)
	{
		ArgumentNullException.ThrowIfNull(pico);

		var provider = BootstrapCommandOptions.ParseProvider(pico.GetParam("--provider"));
		var connectionString = ConnectionStringSource.Parse(pico);
		var actorUsername = pico.GetParam("--actor");
		var username = pico.GetParam("--username");
		var password = pico.GetParamOpt("--password");
		var passwordFromStdin = pico.Contains("--password-stdin");
		var displayName = pico.GetParam("--display-name");
		var rolesRaw = pico.GetParam("--roles");
		var ianaTimeZone = pico.GetParamOpt("--iana-time-zone") ?? DefaultIanaTimeZone;
		var rateRaw = pico.GetParamOpt("--default-hourly-rate");
		var noForcePasswordChange = pico.Contains("--no-force-password-change");
		pico.Finished();

		if (password is not null) {
			throw new AdminCliUsageException(
				"'--password' is not supported because process arguments are not a safe secret channel; use '--password-stdin' or the masked interactive prompt.");
		}

		var roles = ParseRoles(rolesRaw);
		var defaultHourlyRate = ParseDefaultHourlyRate(rateRaw);

		return new() {
			Provider = provider,
			ConnectionString = connectionString,
			ActorUsername = actorUsername,
			Username = username,
			Password = password,
			PasswordFromStdin = passwordFromStdin,
			DisplayName = displayName,
			IanaTimeZone = ianaTimeZone,
			Roles = roles,
			DefaultHourlyRate = defaultHourlyRate,
			ForcePasswordChange = !noForcePasswordChange,
		};
	}

	private static EquatableArray<EmployeeRole> ParseRoles(string rolesRaw)
	{
		var roles = rolesRaw
					.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
					.Select(ParseRole)
					.ToArray();

		return roles.Length == 0
			? throw new AdminCliUsageException("At least one role is required in --roles.")
			: EquatableArray.CopyOf(roles);
	}

	private static EmployeeRole ParseRole(string value) =>
		Enum.TryParse<EmployeeRole>(value, true, out var role) && role != EmployeeRole.None && Enum.IsDefined(role)
			? role
			: throw new AdminCliUsageException(
				$"Unknown role '{value}'. Expected one of: {string.Join(", ", Enum.GetNames<EmployeeRole>().Where(n => n != nameof(EmployeeRole.None)))}.");

	private static decimal? ParseDefaultHourlyRate(string? rateRaw)
	{
		if (rateRaw is null) {
			return null;
		}

		return decimal.TryParse(rateRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate)
			? rate
			: throw new AdminCliUsageException($"Invalid --default-hourly-rate value '{rateRaw}'; expected a decimal number.");
	}
}
