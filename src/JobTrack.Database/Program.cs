namespace JobTrack.Database;

using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Npgsql;

public static class Program
{
	public static async Task<int> Main(string[] args)
	{
		if (args is not ["deploy", .. var rest]) {
			Console.Error.WriteLine(
				"Usage: JobTrack.Database deploy --provider <postgresql|sqlite> --connection-string <connection-string> [--scripts-root <path>]");
			return 1;
		}

		try {
			var options = DeployCommandOptions.Parse(rest);
			await DeployAsync(options, CancellationToken.None).ConfigureAwait(false);
			return 0;
		}
		catch (SchemaDeploymentException ex) {
			Console.Error.WriteLine(ex.Message);
			return 1;
		}
	}

	internal static async Task DeployAsync(DeployCommandOptions options, CancellationToken cancellationToken)
	{
		var scriptsRoot = options.ScriptsRoot ?? DefaultScriptsRoot(options.Provider);
		var scripts = SchemaVersionScriptLoader.Load(scriptsRoot);
		var applicationVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
		var appliedBy = Environment.UserName;

		await using DbConnection connection = options.Provider switch {
			SchemaProvider.PostgreSql => new NpgsqlConnection(options.ConnectionString),
			SchemaProvider.Sqlite => new SqliteConnection(options.ConnectionString),
			_ => throw new SchemaDeploymentException($"Unknown provider '{options.Provider}'."),
		};

		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		if (options.Provider == SchemaProvider.Sqlite) {
			await using var pragmaCommand = connection.CreateCommand();
			pragmaCommand.CommandText =
				"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
			_ = await pragmaCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		(ISchemaVersionStore Store, IDeploymentLockStrategy LockStrategy) components = options.Provider switch {
			SchemaProvider.PostgreSql => (new PostgreSqlSchemaVersionStore(), new PostgreSqlDeploymentLockStrategy()),
			SchemaProvider.Sqlite => (new SqliteSchemaVersionStore(), new SqliteDeploymentLockStrategy()),
			_ => throw new SchemaDeploymentException($"Unknown provider '{options.Provider}'."),
		};

		var deployer = new SchemaDeployer(connection, components.Store, components.LockStrategy, applicationVersion, appliedBy);
		await deployer.DeployAsync(scripts, cancellationToken).ConfigureAwait(false);

		if (options.Provider == SchemaProvider.PostgreSql) {
			await PostgreSqlRolesAndGrants.ApplyAsync(connection, PostgreSqlRolesAndGrantsScriptPath(scriptsRoot), cancellationToken)
				.ConfigureAwait(false);
		}
	}

	private static string DefaultScriptsRoot(SchemaProvider provider)
	{
		var providerDirectoryName = provider switch {
			SchemaProvider.PostgreSql => "postgresql",
			SchemaProvider.Sqlite => "sqlite",
			_ => throw new SchemaDeploymentException($"Unknown provider '{provider}'."),
		};

		return Path.Combine(AppContext.BaseDirectory, "database", providerDirectoryName, "schema-versions");
	}

	/// <summary>
	///     The roles-and-grants script lives as a fixed sibling of <c>schema-versions</c> under
	///     <c>database/postgresql/</c> — derive its path from the effective scripts root (whether
	///     the default or an explicit <c>--scripts-root</c>) rather than <see cref="AppContext.BaseDirectory" />,
	///     which nothing ever populates with a copy of <c>database/</c>.
	/// </summary>
	private static string PostgreSqlRolesAndGrantsScriptPath(string scriptsRoot) =>
		Path.Combine(Directory.GetParent(scriptsRoot)!.FullName, "roles", "jobtrack-roles-and-grants.sql");
}
