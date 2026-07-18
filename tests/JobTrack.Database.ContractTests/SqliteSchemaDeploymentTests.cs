namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using Microsoft.Data.Sqlite;
using TestSupport;

public sealed class SqliteSchemaDeploymentTests()
	: SchemaDeploymentContractTestsBase(new SqliteDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.Sqlite;

	protected override DbConnection CreateConnection(string connectionString) => new SqliteConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new SqliteSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new SqliteDeploymentLockStrategy();
}
