namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using TestSupport;

public sealed class SqlitePersonalAccessTokenSchemaTests()
	: PersonalAccessTokenSchemaContractTestsBase(new SqliteDatabaseFixture())
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

	protected override async Task<long> InsertTokenAsync(
		DbConnection connection, long appUserId, string tokenHash, string label, int expiresInDays)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO personal_access_token (app_user_id, token_hash, label, created_at, expires_at)
							  VALUES (@appUserId, @tokenHash, @label, @createdAt, @expiresAt)
							  RETURNING id;
							  """;
		var now = DateTimeOffset.UtcNow;
		AddParameter(command, "@appUserId", appUserId);
		AddParameter(command, "@tokenHash", tokenHash);
		AddParameter(command, "@label", label);
		AddParameter(command, "@createdAt", EncodeInstant(now));
		AddParameter(command, "@expiresAt", EncodeInstant(now.AddDays(expiresInDays)));

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static long EncodeInstant(DateTimeOffset value) => value.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks;
}
