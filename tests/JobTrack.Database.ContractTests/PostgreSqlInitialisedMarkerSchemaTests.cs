namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlInitialisedMarkerSchemaTests()
	: InitialisedMarkerSchemaContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override object EncodeInitialisedAt(DateTimeOffset value) => value;
}
