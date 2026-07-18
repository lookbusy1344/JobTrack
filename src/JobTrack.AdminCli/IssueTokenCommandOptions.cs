namespace JobTrack.AdminCli;

using System.Globalization;
using PicoArgs_dotnet;

/// <summary>Parsed arguments for the <c>issue-token</c> CLI command.</summary>
public sealed record IssueTokenCommandOptions
{
	/// <summary>
	///     Default token lifetime when <c>--lifetime-days</c> is omitted — short enough that a
	///     forgotten scripting credential does not stay live for long.
	/// </summary>
	private const int DefaultLifetimeDays = 7;

	public required AdminCliProvider Provider { get; init; }

	public required string ConnectionString { get; init; }

	public required string Username { get; init; }

	public required string Label { get; init; }

	public required int LifetimeDays { get; init; }

	/// <summary>
	///     Reads <c>--provider</c>/<c>--connection-string</c>/<c>--username</c>/<c>--label</c>/
	///     <c>--lifetime-days</c> from <paramref name="pico" /> and calls <see cref="PicoArgs.Finished" />
	///     — the caller has already consumed the leading command via <see cref="PicoArgs.GetCommand" />.
	/// </summary>
	public static IssueTokenCommandOptions Parse(PicoArgs pico)
	{
		ArgumentNullException.ThrowIfNull(pico);

		var provider = BootstrapCommandOptions.ParseProvider(pico.GetParam("--provider"));
		var connectionString = pico.GetParam("--connection-string");
		var username = pico.GetParam("--username");
		var label = pico.GetParam("--label");
		var lifetimeDaysRaw = pico.GetParamOpt("--lifetime-days") ?? DefaultLifetimeDays.ToString(CultureInfo.InvariantCulture);
		pico.Finished();

		if (!int.TryParse(lifetimeDaysRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lifetimeDays) || lifetimeDays <= 0) {
			throw new AdminCliUsageException($"Invalid --lifetime-days value '{lifetimeDaysRaw}'; expected a positive integer.");
		}

		return new() {
			Provider = provider,
			ConnectionString = connectionString,
			Username = username,
			Label = label,
			LifetimeDays = lifetimeDays,
		};
	}
}
