namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using TestSupport;

/// <summary>
///     SQLite has no user-defined functions, so each PostgreSQL stored function
///     from the sibling schema-version script becomes a minimal parameterized
///     statement issued directly here (impl plan §6.5). Ancestor-chain node
///     overrides are resolved via a round trip through the same recursive
///     ancestry query as <see cref="SqliteHierarchyAchievementReadinessQueriesSchemaTests" />
///     rather than one nested statement.
/// </summary>
public sealed class SqliteRateResolutionAndBoundaryDiscoverySchemaTests()
	: RateResolutionAndBoundaryDiscoverySchemaContractTestsBase(new SqliteDatabaseFixture())
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

	private static DateTimeOffset DecodeInstant(object value) =>
		new(DateTime.UnixEpoch.Ticks + Convert.ToInt64(value, CultureInfo.InvariantCulture), TimeSpan.Zero);

	protected override async Task<decimal?> ResolveRateAsync(DbConnection connection, long nodeId, long userId, DateTimeOffset at)
	{
		var atEncoded = Convert.ToInt64(EncodeInstant(at), CultureInfo.InvariantCulture);

		var exceptionRate = await ScalarDecimalAsync(
			connection,
			"""
			SELECT rate_override FROM user_schedule_exception
			WHERE user_id = @userId AND effect_id = 1 AND rate_override IS NOT NULL
			  AND started_at <= @at AND finished_at > @at
			LIMIT 1;
			""",
			userId, atEncoded);
		if (exceptionRate is not null) {
			return exceptionRate;
		}

		var ancestry = new List<long> { nodeId };
		ancestry.AddRange(await AncestorsAsync(connection, nodeId));

		foreach (var candidateNodeId in ancestry) {
			var overrideRate = await ScalarDecimalAsync(
				connection,
				"""
				SELECT rate FROM node_rate_override
				WHERE node_id = @nodeId AND user_id = @userId
				  AND effective_start <= @at AND (effective_end IS NULL OR effective_end > @at)
				LIMIT 1;
				""",
				userId, atEncoded, candidateNodeId);
			if (overrideRate is not null) {
				return overrideRate;
			}
		}

		var userRate = await ScalarDecimalAsync(
			connection,
			"""
			SELECT rate FROM user_cost_rate
			WHERE user_id = @userId AND effective_start <= @at AND (effective_end IS NULL OR effective_end > @at)
			LIMIT 1;
			""",
			userId, atEncoded);
		if (userRate is not null) {
			return userRate;
		}

		return await ScalarDecimalAsync(connection, "SELECT default_hourly_rate FROM app_user WHERE id = @userId;", userId, null);
	}

	protected override async Task<IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>> RateBoundariesAsync(
		DbConnection connection, long nodeId, long userId, DateTimeOffset from, DateTimeOffset rangeEnd)
	{
		var fromEncoded = Convert.ToInt64(EncodeInstant(from), CultureInfo.InvariantCulture);
		var toEncoded = Convert.ToInt64(EncodeInstant(rangeEnd), CultureInfo.InvariantCulture);
		var results = new List<(DateTimeOffset, DateTimeOffset)>();

		await using (var command = connection.CreateCommand()) {
			command.CommandText = """
								  SELECT effective_start, COALESCE(effective_end, @to)
								  FROM user_cost_rate
								  WHERE user_id = @userId AND effective_start < @to
								    AND COALESCE(effective_end, @to) > @from;
								  """;
			AddParameter(command, "@userId", userId);
			AddParameter(command, "@from", fromEncoded);
			AddParameter(command, "@to", toEncoded);
			results.AddRange(await ReadBoundaryRangesAsync(command));
		}

		var ancestry = new List<long> { nodeId };
		ancestry.AddRange(await AncestorsAsync(connection, nodeId));

		await using (var command = connection.CreateCommand()) {
			var nodeParameters = string.Join(", ", ancestry.Select((_, index) => $"@node{index}"));
			command.CommandText = $"""
								   SELECT effective_start, COALESCE(effective_end, @to)
								   FROM node_rate_override
								   WHERE user_id = @userId AND node_id IN ({nodeParameters})
								     AND effective_start < @to AND COALESCE(effective_end, @to) > @from;
								   """;
			AddParameter(command, "@userId", userId);
			AddParameter(command, "@from", fromEncoded);
			AddParameter(command, "@to", toEncoded);
			for (var index = 0; index < ancestry.Count; index++) {
				AddParameter(command, $"@node{index}", ancestry[index]);
			}

			results.AddRange(await ReadBoundaryRangesAsync(command));
		}

		await using (var command = connection.CreateCommand()) {
			command.CommandText = """
								  SELECT started_at, finished_at
								  FROM user_schedule_exception
								  WHERE user_id = @userId AND effect_id = 1 AND rate_override IS NOT NULL
								    AND started_at < @to AND finished_at > @from;
								  """;
			AddParameter(command, "@userId", userId);
			AddParameter(command, "@from", fromEncoded);
			AddParameter(command, "@to", toEncoded);
			results.AddRange(await ReadBoundaryRangesAsync(command));
		}

		return results;
	}

	private static async Task<IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>> ReadBoundaryRangesAsync(DbCommand command)
	{
		var results = new List<(DateTimeOffset, DateTimeOffset)>();
		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			results.Add((DecodeInstant(reader.GetValue(0)), DecodeInstant(reader.GetValue(1))));
		}

		return results;
	}

	private static async Task<IReadOnlyList<long>> AncestorsAsync(DbConnection connection, long nodeId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  WITH RECURSIVE ancestry(id) AS (
							      SELECT parent_id FROM job_node WHERE id = @nodeId
							      UNION ALL
							      SELECT jn.parent_id FROM job_node jn JOIN ancestry a ON jn.id = a.id WHERE jn.parent_id IS NOT NULL
							  )
							  SELECT id FROM ancestry WHERE id IS NOT NULL;
							  """;
		AddParameter(command, "@nodeId", nodeId);

		var results = new List<long>();
		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			results.Add(reader.GetInt64(0));
		}

		return results;
	}

	private static async Task<decimal?> ScalarDecimalAsync(DbConnection connection, string commandText, long userId, long? at, long? nodeId = null)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = commandText;
		AddParameter(command, "@userId", userId);
		if (at is not null) {
			AddParameter(command, "@at", at.Value);
		}

		if (nodeId is not null) {
			AddParameter(command, "@nodeId", nodeId.Value);
		}

		var result = await command.ExecuteScalarAsync();
		return result is null or DBNull ? null : Convert.ToDecimal(result, CultureInfo.InvariantCulture);
	}

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}
