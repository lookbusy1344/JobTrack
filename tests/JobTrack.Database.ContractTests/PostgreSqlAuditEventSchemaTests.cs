namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlAuditEventSchemaTests()
	: AuditEventSchemaContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override object EncodeInstant(DateTimeOffset value) => value;

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
							  	(@actorUserId, @occurredAt, @operation, @entityType, @entityId, @correlationId, @reason, @beforeData::jsonb, @afterData::jsonb)
							  RETURNING id;
							  """;
		AddParameter(command, "@actorUserId", actorUserId);
		AddParameter(command, "@occurredAt", EncodeInstant(DateTimeOffset.UtcNow));
		AddParameter(command, "@operation", operation);
		AddParameter(command, "@entityType", entityType);
		AddParameter(command, "@entityId", entityId);
		AddParameter(command, "@correlationId", correlationId);
		AddParameter(command, "@reason", (object?)reason ?? DBNull.Value);
		AddParameter(command, "@beforeData", (object?)beforeData ?? DBNull.Value);
		AddParameter(command, "@afterData", (object?)afterData ?? DBNull.Value);

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}
}
