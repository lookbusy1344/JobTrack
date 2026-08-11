namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlWorkerOverlapCandidateDiscoverySchemaTests()
	: WorkerOverlapCandidateDiscoverySchemaContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override object EncodeInstant(DateTimeOffset value) => value;

	protected override DateTimeOffset DecodeInstant(object value) => new((DateTime)value, TimeSpan.Zero);

	protected override async Task<IReadOnlyList<OverlapCandidate>> OverlapCandidatesAsync(DbConnection connection, long userId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT session_id, leaf_work_id, started_at, finished_at, effective_finished_at
							  FROM worker_overlapping_sessions(@userId, @queryStart, @queryEnd, @asOf);
							  """;
		AddParameter(command, "@userId", userId);
		AddParameter(command, "@queryStart", QueryStartInstant);
		AddParameter(command, "@queryEnd", QueryEndInstant);
		AddParameter(command, "@asOf", AsOfInstant);

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
