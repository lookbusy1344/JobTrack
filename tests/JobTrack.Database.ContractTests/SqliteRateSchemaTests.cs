namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using TestSupport;

public sealed class SqliteRateSchemaTests()
	: RateSchemaContractTestsBase(new SqliteDatabaseFixture())
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

	protected override object EncodeRate(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
