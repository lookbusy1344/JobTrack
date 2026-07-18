namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlPersonalAccessTokenSchemaTests()
	: PersonalAccessTokenSchemaContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

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
		AddParameter(command, "@createdAt", now);
		AddParameter(command, "@expiresAt", now.AddDays(expiresInDays));

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}
}
