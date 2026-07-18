namespace JobTrack.Database;

/// <summary>
///     Parsed arguments for the <c>deploy</c> CLI command.
/// </summary>
public sealed record DeployCommandOptions
{
	public required SchemaProvider Provider { get; init; }

	public required string ConnectionString { get; init; }

	public string? ScriptsRoot { get; init; }

	public static DeployCommandOptions Parse(IReadOnlyList<string> args)
	{
		SchemaProvider? provider = null;
		string? connectionString = null;
		string? scriptsRoot = null;

		for (var index = 0; index < args.Count; index += 2) {
			if (index + 1 >= args.Count) {
				throw new SchemaDeploymentException($"Flag '{args[index]}' is missing its value.");
			}

			var flag = args[index];
			var value = args[index + 1];

			switch (flag) {
				case "--provider":
					provider = ParseProvider(value);
					break;
				case "--connection-string":
					connectionString = value;
					break;
				case "--scripts-root":
					scriptsRoot = value;
					break;
				default:
					throw new SchemaDeploymentException($"Unknown flag '{flag}'.");
			}
		}

		if (provider is null) {
			throw new SchemaDeploymentException("Missing required flag '--provider'.");
		}

		if (connectionString is null) {
			throw new SchemaDeploymentException("Missing required flag '--connection-string'.");
		}

		return new() { Provider = provider.Value, ConnectionString = connectionString, ScriptsRoot = scriptsRoot };
	}

	private static SchemaProvider ParseProvider(string value) => value switch {
		"postgresql" => SchemaProvider.PostgreSql,
		"sqlite" => SchemaProvider.Sqlite,
		_ => throw new SchemaDeploymentException($"Unknown provider '{value}'. Expected 'postgresql' or 'sqlite'."),
	};
}
