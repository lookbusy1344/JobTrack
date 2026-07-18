namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using TestSupport;

public sealed class SqliteAuditEventSchemaTests()
	: AuditEventSchemaContractTestsBase(new SqliteDatabaseFixture())
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

	protected override async Task<long> InsertAuditEventAsync(
		DbConnection connection,
		long actorUserId,
		string operation,
		string entityType,
		long entityId,
		Guid correlationId,
		string? reason,
		string? beforeData,
		string? afterData)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO audit_event
							  	(actor_user_id, occurred_at, operation, entity_type, entity_id, correlation_id, reason, before_data, after_data)
							  VALUES
							  	(@actorUserId, @occurredAt, @operation, @entityType, @entityId, @correlationId, @reason, @beforeData, @afterData)
							  RETURNING id;
							  """;
		AddParameter(command, "@actorUserId", actorUserId);
		AddParameter(command, "@occurredAt", EncodeInstant(DateTimeOffset.UtcNow));
		AddParameter(command, "@operation", operation);
		AddParameter(command, "@entityType", entityType);
		AddParameter(command, "@entityId", entityId);
		AddParameter(command, "@correlationId", correlationId.ToString());
		AddParameter(command, "@reason", (object?)reason ?? DBNull.Value);
		AddParameter(command, "@beforeData", (object?)beforeData ?? DBNull.Value);
		AddParameter(command, "@afterData", (object?)afterData ?? DBNull.Value);

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}
}
