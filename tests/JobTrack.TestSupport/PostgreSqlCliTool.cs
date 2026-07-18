namespace JobTrack.TestSupport;

using System.Diagnostics;
using System.Globalization;
using Npgsql;

/// <summary>
///     Invokes the <c>pg_dump</c>/<c>pg_restore</c> command-line tools against a
///     fixture database, for the §6.7 gate's schema-level PostgreSQL
///     backup/restore smoke test (see <c>PostgreSqlBackupRestoreTests</c> and
///     docs/operations/postgresql-backup-restore.md). Not production code --
///     backup/restore is an operational procedure run by whoever administers a
///     deployment, not something the application performs itself.
/// </summary>
public static class PostgreSqlCliTool
{
	private const string PgDumpPathEnvironmentVariable = "JOBTRACK_TEST_PG_DUMP_PATH";
	private const string PgRestorePathEnvironmentVariable = "JOBTRACK_TEST_PG_RESTORE_PATH";

	public static Task DumpAsync(string connectionString, string outputFilePath, CancellationToken cancellationToken) =>
		RunAsync(
			Environment.GetEnvironmentVariable(PgDumpPathEnvironmentVariable) ?? "pg_dump",
			connectionString,
			[.. ConnectionArguments(connectionString), "--format=custom", $"--file={outputFilePath}"],
			cancellationToken);

	public static Task RestoreAsync(string connectionString, string inputFilePath, CancellationToken cancellationToken) =>
		RunAsync(
			Environment.GetEnvironmentVariable(PgRestorePathEnvironmentVariable) ?? "pg_restore",
			connectionString,
			[.. ConnectionArguments(connectionString), inputFilePath],
			cancellationToken);

	private static IReadOnlyList<string> ConnectionArguments(string connectionString)
	{
		var builder = new NpgsqlConnectionStringBuilder(connectionString);

		return [
			"--host", builder.Host ?? "localhost",
			"--port", builder.Port.ToString(CultureInfo.InvariantCulture),
			"--username", builder.Username ?? Environment.UserName,
			"--dbname", builder.Database ?? throw new ArgumentException("Connection string has no database.", nameof(connectionString)),
		];
	}

	private static async Task RunAsync(
		string fileName, string connectionString, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		var startInfo = new ProcessStartInfo(fileName) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };

		foreach (var argument in arguments) {
			startInfo.ArgumentList.Add(argument);
		}

		var password = new NpgsqlConnectionStringBuilder(connectionString).Password;
		if (!string.IsNullOrEmpty(password)) {
			startInfo.Environment["PGPASSWORD"] = password;
		}

		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
		var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
		var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
		await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
		var standardError = await standardErrorTask.ConfigureAwait(false);
		_ = await standardOutputTask.ConfigureAwait(false);

		if (process.ExitCode != 0) {
			throw new InvalidOperationException($"'{fileName}' exited with code {process.ExitCode}: {standardError}");
		}
	}
}
