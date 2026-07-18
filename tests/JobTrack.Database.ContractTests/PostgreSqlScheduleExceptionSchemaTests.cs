namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlScheduleExceptionSchemaTests()
	: ScheduleExceptionSchemaContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override object EncodeInstant(DateTimeOffset value) => value;

	protected override object EncodeRate(decimal value) => value;

	protected override async Task AssertExceptionRangeAsync(DbConnection connection, long exceptionId, DateTimeOffset startedAt,
		DateTimeOffset finishedAt)
	{
		var range = await PostgreSqlGeneratedRangeAssertions.ReadTstzRangeAsync(
			connection, "user_schedule_exception", "exception_range", exceptionId);

		PostgreSqlGeneratedRangeAssertions.AssertMatches(range, startedAt, finishedAt);
	}
}
