namespace JobTrack.AdminCli;

using System.Globalization;
using PicoArgs_dotnet;

/// <summary>Parsed arguments for the <c>import-tree</c> CLI command.</summary>
public sealed record JobTreeImportCommandOptions
{
	/// <summary>
	///     Default <c>--parent-id</c> when omitted: the permanent root's <c>job_node</c> id (ADR 0006,
	///     enforced unique by <c>job_node_single_root_idx</c>), so an import with no explicit anchor lands at
	///     the top of the tree.
	/// </summary>
	private const long DefaultParentJobNodeId = 1;

	public required AdminCliProvider Provider { get; init; }

	public required string ConnectionString { get; init; }

	public required string Username { get; init; }

	public required string FilePath { get; init; }

	public required long ParentJobNodeId { get; init; }

	/// <summary>
	///     Reads <c>--provider</c>/<c>--connection-string</c>/<c>--username</c>/<c>--file</c>/
	///     <c>--parent-id</c> from <paramref name="pico" /> and calls <see cref="PicoArgs.Finished" /> — the
	///     caller has already consumed the leading command via <see cref="PicoArgs.GetCommand" />.
	/// </summary>
	public static JobTreeImportCommandOptions Parse(PicoArgs pico)
	{
		ArgumentNullException.ThrowIfNull(pico);

		var provider = BootstrapCommandOptions.ParseProvider(pico.GetParam("--provider"));
		var connectionString = pico.GetParam("--connection-string");
		var username = pico.GetParam("--username");
		var filePath = pico.GetParam("--file");
		var parentIdRaw = pico.GetParamOpt("--parent-id") ?? DefaultParentJobNodeId.ToString(CultureInfo.InvariantCulture);
		pico.Finished();

		if (!long.TryParse(parentIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentId) || parentId <= 0) {
			throw new AdminCliUsageException($"Invalid --parent-id value '{parentIdRaw}'; expected a positive integer.");
		}

		return new() {
			Provider = provider,
			ConnectionString = connectionString,
			Username = username,
			FilePath = filePath,
			ParentJobNodeId = parentId,
		};
	}
}
