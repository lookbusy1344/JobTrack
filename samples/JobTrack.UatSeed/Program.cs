namespace JobTrack.UatSeed;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Microsoft.Data.Sqlite;
using Npgsql;
using Persistence.PostgreSql;
using Persistence.Sqlite;

internal static class Program
{
	private const string ConfigureSqliteConnectionSql =
		"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";

	private const string UsageMessage =
		"Usage: JobTrack.UatSeed --provider <postgresql|sqlite> --connection-string <connection-string>\n\n" +
		"Seeds a synthetic end-user-testing scenario into an already-deployed, already-bootstrapped\n" +
		"database (run 'JobTrack.Database deploy' then 'JobTrack.AdminCli bootstrap' first — see\n" +
		"README.md \"Running on a development server\"). Every seeded employee's password is\n" +
		"'" + UatSeeder.KnownPassword + "' and forces a change at first sign-in.";

	public static async Task<int> Main(string[] args)
	{
		var options = ParseArgs(args);
		if (options is null) {
			Console.Error.WriteLine(UsageMessage);
			return 1;
		}

		var (provider, connectionString) = options.Value;

		await using DbConnection connection = provider == "postgresql"
			? new NpgsqlConnection(connectionString)
			: new SqliteConnection(connectionString);
		await connection.OpenAsync();
		if (provider == "sqlite") {
			await using var pragma = connection.CreateCommand();
			pragma.CommandText = ConfigureSqliteConnectionSql;
			_ = await pragma.ExecuteNonQueryAsync();
		}

		await using var rootOwnerCommand = connection.CreateCommand();
		rootOwnerCommand.CommandText = "SELECT owner_user_id FROM job_node WHERE parent_id IS NULL;";
		var administratorId = new AppUserId(
			Convert.ToInt64(await rootOwnerCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture));

		var client = provider == "postgresql"
			? JobTrackPostgreSql.Create(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build())
			: JobTrackSqlite.Create(connectionString);

		var summary = await UatSeeder.SeedAsync(client, connection, administratorId);

		WriteSummary(summary);
		return 0;
	}

	private static void WriteSummary(UatSeedSummary summary)
	{
		Console.WriteLine("UAT scenario seeded.");
		Console.WriteLine($"Job manager:            priya.manager (id {Id(summary.JobManagerId.Value)})");
		Console.WriteLine($"Worker:                 wendy.worker (id {Id(summary.WorkerId.Value)})");
		Console.WriteLine($"Requester:              rita.requester (id {Id(summary.RequesterId.Value)})");
		Console.WriteLine($"Every seeded password:  {UatSeeder.KnownPassword}");
		Console.WriteLine($"Unassigned request:     job node {Id(summary.UnassignedRequestNodeId.Value)}");
		Console.WriteLine($"Assigned/ack'd request: job node {Id(summary.AssignedRequestNodeId.Value)}");
		Console.WriteLine($"Pickup-pool leaf:       job node {Id(summary.PoolLeafNodeId.Value)}");
		Console.WriteLine($"Prerequisite-blocked:   job node {Id(summary.BlockedLeafNodeId.Value)}");
		Console.WriteLine($"Active session:         job node {Id(summary.ActiveSessionLeafNodeId.Value)}");
		Console.WriteLine($"Cost-reportable:        job node {Id(summary.CostReportableLeafNodeId.Value)}");
	}

	private static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);

	private static (string Provider, string ConnectionString)? ParseArgs(string[] arguments)
	{
		string? provider = null;
		string? connectionString = null;

		for (var i = 0; i < arguments.Length - 1; i++) {
			switch (arguments[i]) {
				case "--provider":
					provider = arguments[++i];
					break;
				case "--connection-string":
					connectionString = arguments[++i];
					break;
			}
		}

		if (provider is not ("postgresql" or "sqlite") || string.IsNullOrWhiteSpace(connectionString)) {
			return null;
		}

		return (provider, connectionString);
	}
}
