namespace JobTrack.AdminCli;

using System.Globalization;
using PicoArgs_dotnet;

/// <summary>Parsed arguments for the <c>set-home-node</c> CLI command.</summary>
public sealed record SetHomeNodeCommandOptions
{
	public required AdminCliProvider Provider { get; init; }

	public required string ConnectionString { get; init; }

	/// <summary>
	///     The employee whose own home node is being set. The command runs as this account, not on its
	///     behalf — see <see cref="SetHomeNodeCommand" />.
	/// </summary>
	public required string Username { get; init; }

	/// <summary>
	///     The branch to land on after login, or <see langword="null" /> when <c>--clear</c> was passed
	///     (reset to the tree root).
	/// </summary>
	public required long? JobNodeId { get; init; }

	/// <summary>
	///     Reads <c>--provider</c>/<c>--connection-string</c>/<c>--username</c> and exactly one of
	///     <c>--node-id</c>/<c>--clear</c> from <paramref name="pico" />, then calls
	///     <see cref="PicoArgs.Finished" /> — the caller has already consumed the leading command via
	///     <see cref="PicoArgs.GetCommand" />.
	/// </summary>
	public static SetHomeNodeCommandOptions Parse(PicoArgs pico)
	{
		ArgumentNullException.ThrowIfNull(pico);

		var provider = BootstrapCommandOptions.ParseProvider(pico.GetParam("--provider"));
		var connectionString = ConnectionStringSource.Parse(pico);
		var username = pico.GetParam("--username");
		var nodeIdRaw = pico.GetParamOpt("--node-id");
		var clear = pico.Contains("--clear");
		pico.Finished();

		if (clear && nodeIdRaw is not null) {
			throw new AdminCliUsageException("'--node-id' and '--clear' are mutually exclusive; pass exactly one.");
		}

		if (!clear && nodeIdRaw is null) {
			throw new AdminCliUsageException("One of '--node-id <job-node-id>' or '--clear' is required.");
		}

		return new() {
			Provider = provider,
			ConnectionString = connectionString,
			Username = username,
			JobNodeId = clear ? null : ParseNodeId(nodeIdRaw!),
		};
	}

	private static long ParseNodeId(string nodeIdRaw) =>
		long.TryParse(nodeIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nodeId) && nodeId > 0
			? nodeId
			: throw new AdminCliUsageException($"Invalid --node-id value '{nodeIdRaw}'; expected a positive integer.");
}
