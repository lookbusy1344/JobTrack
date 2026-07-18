namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlWorkSessionSchemaTests()
	: WorkSessionSchemaContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override object EncodeInstant(DateTimeOffset value) => value;

	protected override async Task AssertSessionRangeAsync(
		DbConnection connection, long sessionId, DateTimeOffset startedAt, DateTimeOffset? finishedAt)
	{
		var range = await PostgreSqlGeneratedRangeAssertions.ReadTstzRangeAsync(connection, "work_session", "session_range", sessionId);

		PostgreSqlGeneratedRangeAssertions.AssertMatches(range, startedAt, finishedAt);
	}
}
