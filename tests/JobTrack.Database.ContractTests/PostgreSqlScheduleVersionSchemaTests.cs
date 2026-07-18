namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlScheduleVersionSchemaTests()
	: ScheduleVersionSchemaContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override object EncodeInstant(DateTimeOffset value) => value;

	protected override object EncodeDate(DateOnly value) => value;

	protected override object EncodeTimeOfDay(TimeOnly value) => value;

	protected override async Task AssertEffectiveRangeAsync(
		DbConnection connection, long scheduleVersionId, DateOnly effectiveStart, DateOnly? effectiveEnd)
	{
		var range = await PostgreSqlGeneratedRangeAssertions.ReadDateRangeAsync(
			connection, "user_schedule_version", "effective_range", scheduleVersionId);

		PostgreSqlGeneratedRangeAssertions.AssertMatches(range, effectiveStart, effectiveEnd);
	}
}
