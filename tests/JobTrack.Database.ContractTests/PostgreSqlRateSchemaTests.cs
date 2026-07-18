namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlRateSchemaTests()
	: RateSchemaContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override object EncodeInstant(DateTimeOffset value) => value;

	protected override object EncodeRate(decimal value) => value;

	protected override async Task AssertUserCostRateRangeAsync(
		DbConnection connection, long userCostRateId, DateTimeOffset effectiveStart, DateTimeOffset? effectiveEnd)
	{
		var range = await PostgreSqlGeneratedRangeAssertions.ReadTstzRangeAsync(connection, "user_cost_rate", "effective_range", userCostRateId);

		PostgreSqlGeneratedRangeAssertions.AssertMatches(range, effectiveStart, effectiveEnd);
	}

	protected override async Task AssertNodeRateOverrideRangeAsync(
		DbConnection connection, long nodeRateOverrideId, DateTimeOffset effectiveStart, DateTimeOffset? effectiveEnd)
	{
		var range = await PostgreSqlGeneratedRangeAssertions.ReadTstzRangeAsync(
			connection, "node_rate_override", "effective_range", nodeRateOverrideId);

		PostgreSqlGeneratedRangeAssertions.AssertMatches(range, effectiveStart, effectiveEnd);
	}
}
