namespace JobTrack.AdminCli;

using PicoArgs_dotnet;

/// <summary>Parsed arguments for the <c>bootstrap</c> CLI command.</summary>
public sealed record BootstrapCommandOptions
{
	public required AdminCliProvider Provider { get; init; }

	public required string ConnectionString { get; init; }

	/// <summary>
	///     The resolved administrator password. Argument parsing always leaves this null; the host
	///     resolves it from standard input or the masked interactive prompt before invoking the command.
	/// </summary>
	public string? Password { get; init; }

	/// <summary>
	///     <see langword="true" /> when <c>--password-stdin</c> is passed: the administrator password is
	///     read as one line from standard input (security review remediation §2.7 -- the
	///     <c>docker login --password-stdin</c> convention), for scripted/automated bootstrap that still
	///     avoids putting the secret in <c>argv</c>/shell history.
	/// </summary>
	public bool PasswordFromStdin { get; init; }

	/// <summary>
	///     <see langword="false" /> when <c>--no-force-password-change</c> is passed, clearing the
	///     ADR 0023 forced-password-change on the new administrator after bootstrap. Its only use is the
	///     container demo's admin account, whose baked-in credential resets to the same value on every
	///     recycle — a forced change there is pointless friction. <see langword="true" /> otherwise (the
	///     normal secure default).
	/// </summary>
	public bool ForcePasswordChange { get; init; } = true;

	/// <summary>
	///     Reads <c>--provider</c>/<c>--connection-string</c>/<c>--password-stdin</c>/
	///     <c>--no-force-password-change</c> from <paramref name="pico" /> and calls
	///     <see cref="PicoArgs.Finished" /> — the caller has already consumed the leading command via
	///     <see cref="PicoArgs.GetCommand" />.
	/// </summary>
	public static BootstrapCommandOptions Parse(PicoArgs pico)
	{
		ArgumentNullException.ThrowIfNull(pico);

		var provider = ParseProvider(pico.GetParam("--provider"));
		var connectionString = ConnectionStringSource.Parse(pico);
		var password = pico.GetParamOpt("--password");
		var passwordFromStdin = pico.Contains("--password-stdin");
		var noForcePasswordChange = pico.Contains("--no-force-password-change");
		pico.Finished();

		if (password is not null) {
			throw new AdminCliUsageException(
				"'--password' is not supported because process arguments are not a safe secret channel; use '--password-stdin' or the masked interactive prompt.");
		}

		return new() {
			Provider = provider,
			ConnectionString = connectionString,
			Password = password,
			PasswordFromStdin = passwordFromStdin,
			ForcePasswordChange = !noForcePasswordChange,
		};
	}

	internal static AdminCliProvider ParseProvider(string value) => value switch {
		"postgresql" => AdminCliProvider.PostgreSql,
		"sqlite" => AdminCliProvider.Sqlite,
		_ => throw new AdminCliUsageException($"Unknown provider '{value}'. Expected 'postgresql' or 'sqlite'."),
	};
}
