namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using TestSupport;

public sealed class PostgreSqlRateResolutionAndBoundaryDiscoverySchemaTests()
	: RateResolutionAndBoundaryDiscoverySchemaContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override object EncodeInstant(DateTimeOffset value) => value;

	protected override async Task<decimal?> ResolveRateAsync(DbConnection connection, long nodeId, long userId, DateTimeOffset at)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT resolve_rate(@nodeId, @userId, @at);";
		AddParameter(command, "@nodeId", nodeId);
		AddParameter(command, "@userId", userId);
		AddParameter(command, "@at", at);

		var result = await command.ExecuteScalarAsync();
		return result is null or DBNull ? null : (decimal)result;
	}

	protected override async Task<IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>> RateBoundariesAsync(
		DbConnection connection, long nodeId, long userId, DateTimeOffset from, DateTimeOffset rangeEnd)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT effective FROM user_rate_boundaries(@userId, @nodeId, @from, @to);";
		AddParameter(command, "@userId", userId);
		AddParameter(command, "@nodeId", nodeId);
		AddParameter(command, "@from", from);
		AddParameter(command, "@to", rangeEnd);

		var results = new List<(DateTimeOffset, DateTimeOffset)>();
		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			var range = reader.GetFieldValue<NpgsqlRange<DateTime>>(0);
			results.Add((new(range.LowerBound, TimeSpan.Zero), new(range.UpperBound, TimeSpan.Zero)));
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
