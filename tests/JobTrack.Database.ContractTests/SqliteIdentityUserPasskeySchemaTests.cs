namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using Microsoft.Data.Sqlite;
using TestSupport;

public sealed class SqliteIdentityUserPasskeySchemaTests()
	: IdentityUserPasskeySchemaContractTestsBase(new SqliteDatabaseFixture())
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

	protected override async Task InsertPasskeyAsync(
		DbConnection connection,
		long identityUserId,
		byte[] credentialId,
		string name,
		string normalizedName,
		long signCount = 0,
		bool isUserVerified = true,
		byte[]? aaguid = null,
		byte[]? publicKey = null,
		string? transports = null,
		byte[]? attestationObject = null,
		byte[]? clientDataJson = null)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO identity_user_passkey
							      (credential_id, identity_user_id, name, normalized_name, public_key, created_at, sign_count,
							       transports, is_user_verified, is_backup_eligible, is_backed_up, aaguid, attestation_object, client_data_json)
							  VALUES (@credentialId, @identityUserId, @name, @normalizedName, @publicKey, @createdAt, @signCount,
							          @transports, @isUserVerified, 0, 0, @aaguid, @attestationObject, @clientDataJson);
							  """;
		AddParameter(command, "@credentialId", credentialId);
		AddParameter(command, "@identityUserId", identityUserId);
		AddParameter(command, "@name", name);
		AddParameter(command, "@normalizedName", normalizedName);
		AddParameter(command, "@publicKey", publicKey ?? new byte[] {
			10, 20, 30,
		});
		AddParameter(command, "@createdAt", DateTimeOffset.UtcNow.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks);
		AddParameter(command, "@signCount", signCount);
		AddParameter(command, "@transports", transports ?? (object)DBNull.Value);
		AddParameter(command, "@isUserVerified", isUserVerified ? 1 : 0);
		AddParameter(command, "@aaguid", aaguid ?? (object)DBNull.Value);
		AddParameter(command, "@attestationObject", attestationObject ?? new byte[] {
			1,
		});
		AddParameter(command, "@clientDataJson", clientDataJson ?? new byte[] {
			1,
		});
		_ = await command.ExecuteNonQueryAsync();
	}
}
