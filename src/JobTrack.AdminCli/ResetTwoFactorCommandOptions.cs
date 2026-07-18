namespace JobTrack.AdminCli;

using PicoArgs_dotnet;

/// <summary>Parsed arguments for the <c>reset-2fa</c> CLI command.</summary>
public sealed record ResetTwoFactorCommandOptions
{
	public required AdminCliProvider Provider { get; init; }

	public required string ConnectionString { get; init; }

	public required string Username { get; init; }

	/// <summary>
	///     Reads <c>--provider</c>/<c>--connection-string</c>/<c>--username</c> from
	///     <paramref name="pico" /> and calls <see cref="PicoArgs.Finished" /> — the caller has already
	///     consumed the leading command via <see cref="PicoArgs.GetCommand" />.
	/// </summary>
	public static ResetTwoFactorCommandOptions Parse(PicoArgs pico)
	{
		ArgumentNullException.ThrowIfNull(pico);

		var provider = BootstrapCommandOptions.ParseProvider(pico.GetParam("--provider"));
		var connectionString = pico.GetParam("--connection-string");
		var username = pico.GetParam("--username");
		pico.Finished();

		return new() { Provider = provider, ConnectionString = connectionString, Username = username };
	}
}
