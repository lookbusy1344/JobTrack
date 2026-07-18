namespace JobTrack.AdminCli;

using PicoArgs_dotnet;

/// <summary>Parsed arguments for the <c>bootstrap</c> CLI command.</summary>
public sealed record BootstrapCommandOptions
{
	public required AdminCliProvider Provider { get; init; }

	public required string ConnectionString { get; init; }

	/// <summary>
	///     The administrator password, when supplied non-interactively for scripted/automated
	///     bootstrap (e.g. CI, container image build steps). <see langword="null" /> when omitted, in
	///     which case <see cref="BootstrapCommand" /> falls back to its normal masked interactive prompt.
	///     Deliberately opt-in: a value here is visible in the process list and shell history, an
	///     explicit trade-off the caller accepts by passing it, not this command's default posture.
	/// </summary>
	public string? Password { get; init; }

	/// <summary>
	///     <see langword="false" /> when <c>--no-force-password-change</c> is passed, clearing the
	///     ADR 0023 forced-password-change on the new administrator after bootstrap. Its only use is the
	///     container demo's admin account, whose baked-in credential resets to the same value on every
	///     recycle — a forced change there is pointless friction. <see langword="true" /> otherwise (the
	///     normal secure default).
	/// </summary>
	public bool ForcePasswordChange { get; init; } = true;

	/// <summary>
	///     Reads <c>--provider</c>/<c>--connection-string</c>/<c>--password</c>/
	///     <c>--no-force-password-change</c> from <paramref name="pico" /> and calls
	///     <see cref="PicoArgs.Finished" /> — the caller has already consumed the leading command via
	///     <see cref="PicoArgs.GetCommand" />.
	/// </summary>
	public static BootstrapCommandOptions Parse(PicoArgs pico)
	{
		ArgumentNullException.ThrowIfNull(pico);

		var provider = ParseProvider(pico.GetParam("--provider"));
		var connectionString = pico.GetParam("--connection-string");
		var password = pico.GetParamOpt("--password");
		var noForcePasswordChange = pico.Contains("--no-force-password-change");
		pico.Finished();

		return new() {
			Provider = provider,
			ConnectionString = connectionString,
			Password = password,
			ForcePasswordChange = !noForcePasswordChange,
		};
	}

	internal static AdminCliProvider ParseProvider(string value) => value switch {
		"postgresql" => AdminCliProvider.PostgreSql,
		"sqlite" => AdminCliProvider.Sqlite,
		_ => throw new AdminCliUsageException($"Unknown provider '{value}'. Expected 'postgresql' or 'sqlite'."),
	};
}
