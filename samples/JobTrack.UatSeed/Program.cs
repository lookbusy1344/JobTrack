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
		"Usage: JobTrack.UatSeed --provider <postgresql|sqlite> --connection-string <connection-string>\n" +
		"       JobTrack.UatSeed --provider <postgresql|sqlite> --connection-string <connection-string>\n" +
		"           --requester-demo --requester-username <username> --job-manager-username <username>\n\n" +
		"Seeds a synthetic end-user-testing scenario into an already-deployed, already-bootstrapped\n" +
		"database (run 'JobTrack.Database deploy' then 'JobTrack.AdminCli bootstrap' first — see\n" +
		"README.md \"Running on a development server\"). The requester-demo mode uses two existing\n" +
		"accounts and creates six genuine requests spanning open and closed states. In the default\n" +
		"scenario every seeded employee's password is\n" +
		"'" + UatSeeder.KnownPassword + "' and forces a change at first sign-in.";

	public static async Task<int> Main(string[] args)
	{
		var options = ParseArgs(args);
		if (options is null) {
			Console.Error.WriteLine(UsageMessage);
			return 1;
		}

		var provider = options.Provider;
		var connectionString = options.ConnectionString;

		await using DbConnection connection = provider == "postgresql"
			? new NpgsqlConnection(connectionString)
			: new SqliteConnection(connectionString);
		await connection.OpenAsync();
		if (provider == "sqlite") {
			await using var pragma = connection.CreateCommand();
			pragma.CommandText = ConfigureSqliteConnectionSql;
			_ = await pragma.ExecuteNonQueryAsync();
		}

		var client = provider == "postgresql"
			? JobTrackPostgreSql.Create(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build())
			: JobTrackSqlite.Create(connectionString);

		if (options.RequesterDemo) {
			var requesterId = await FindUserIdAsync(connection, options.RequesterUserName!);
			var jobManagerId = await FindUserIdAsync(connection, options.JobManagerUserName!);
			var requesterSummary = await UatSeeder.SeedRequesterDemoAsync(client, connection, jobManagerId, requesterId);
			WriteRequesterDemoSummary(requesterSummary, options.RequesterUserName!);
			return 0;
		}

		await using var rootOwnerCommand = connection.CreateCommand();
		rootOwnerCommand.CommandText = "SELECT owner_user_id FROM job_node WHERE parent_id IS NULL;";
		var administratorId = new AppUserId(
			Convert.ToInt64(await rootOwnerCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
		var summary = await UatSeeder.SeedAsync(client, connection, administratorId);

		WriteSummary(summary);
		return 0;
	}

	private static async Task<AppUserId> FindUserIdAsync(DbConnection connection, string userName)
	{
		await using var command = connection.CreateCommand();
		command.CommandText =
			"SELECT app_user_id FROM identity_user WHERE normalized_user_name = @normalizedUserName;";
		var parameter = command.CreateParameter();
		parameter.ParameterName = "@normalizedUserName";
		parameter.Value = userName.ToUpperInvariant();
		_ = command.Parameters.Add(parameter);
		var value = await command.ExecuteScalarAsync()
					?? throw new InvalidOperationException($"Account '{userName}' does not exist.");
		return new(Convert.ToInt64(value, CultureInfo.InvariantCulture));
	}

	private static void WriteRequesterDemoSummary(RequesterDemoSeedSummary summary, string requesterUserName)
	{
		Console.WriteLine($"Requester demo seeded for {requesterUserName}.");
		foreach (var nodeId in summary.RequestNodeIds) {
			Console.WriteLine($"Request job node: {Id(nodeId.Value)}");
		}
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

	private static SeedOptions? ParseArgs(string[] arguments)
	{
		string? provider = null;
		string? connectionString = null;
		string? requesterUserName = null;
		string? jobManagerUserName = null;
		var requesterDemo = false;

		for (var i = 0; i < arguments.Length; ++i) {
			switch (arguments[i]) {
				case "--provider" when i + 1 < arguments.Length:
					provider = arguments[++i];
					break;
				case "--connection-string" when i + 1 < arguments.Length:
					connectionString = arguments[++i];
					break;
				case "--requester-demo":
					requesterDemo = true;
					break;
				case "--requester-username" when i + 1 < arguments.Length:
					requesterUserName = arguments[++i];
					break;
				case "--job-manager-username" when i + 1 < arguments.Length:
					jobManagerUserName = arguments[++i];
					break;
			}
		}

		if (provider is not ("postgresql" or "sqlite") || string.IsNullOrWhiteSpace(connectionString) ||
			(requesterDemo && (string.IsNullOrWhiteSpace(requesterUserName) || string.IsNullOrWhiteSpace(jobManagerUserName)))) {
			return null;
		}

		return new(provider, connectionString, requesterDemo, requesterUserName, jobManagerUserName);
	}

	private sealed record SeedOptions(
		string Provider,
		string ConnectionString,
		bool RequesterDemo,
		string? RequesterUserName,
		string? JobManagerUserName);
}
