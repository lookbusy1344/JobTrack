namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using TestSupport;

/// <summary>
///     SQLite has no user-defined functions, so the PostgreSQL
///     <c>worker_overlapping_sessions</c> stored function becomes a minimal
///     parameterized query issued directly here (impl plan §6.5).
/// </summary>
public sealed class SqliteWorkerOverlapCandidateDiscoverySchemaTests()
	: WorkerOverlapCandidateDiscoverySchemaContractTestsBase(new SqliteDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.Sqlite;

	protected override DbConnection CreateConnection(string connectionString) => new SqliteConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new SqliteSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new SqliteDeploymentLockStrategy();

	protected override async Task PrepareConnectionAsync(DbConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
		_ = await command.ExecuteNonQueryAsync();
	}

	protected override object EncodeInstant(DateTimeOffset value) => value.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks;

	protected override DateTimeOffset DecodeInstant(object value) =>
		new(DateTime.UnixEpoch.Ticks + Convert.ToInt64(value, CultureInfo.InvariantCulture), TimeSpan.Zero);

	protected override async Task<IReadOnlyList<OverlapCandidate>> OverlapCandidatesAsync(DbConnection connection, long userId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT id, leaf_work_id, started_at, finished_at, COALESCE(finished_at, @asOf) AS effective_finished_at
							  FROM work_session
							  WHERE worked_by_user_id = @userId
							    AND started_at < @queryEnd
							    AND (finished_at IS NULL OR finished_at > @queryStart);
							  """;
		AddParameter(command, "@userId", userId);
		AddParameter(command, "@queryStart", EncodeInstant(QueryStartInstant));
		AddParameter(command, "@queryEnd", EncodeInstant(QueryEndInstant));
		AddParameter(command, "@asOf", EncodeInstant(AsOfInstant));

		var results = new List<OverlapCandidate>();
		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			results.Add(new(
				reader.GetInt64(0),
				reader.GetInt64(1),
				DecodeInstant(reader.GetValue(2)),
				reader.IsDBNull(3) ? null : DecodeInstant(reader.GetValue(3)),
				DecodeInstant(reader.GetValue(4))));
		}

		return results;
	}

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}
