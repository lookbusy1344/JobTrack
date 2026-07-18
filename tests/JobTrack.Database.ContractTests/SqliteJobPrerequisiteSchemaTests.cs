namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using Microsoft.Data.Sqlite;
using TestSupport;

public sealed class SqliteJobPrerequisiteSchemaTests()
	: JobPrerequisiteSchemaContractTestsBase(new SqliteDatabaseFixture())
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

	protected override async Task AddPrerequisiteAsync(DbConnection connection, long fromId, long toId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "INSERT INTO job_prerequisite (from_id, to_id) VALUES (@fromId, @toId);";

		var fromParameter = command.CreateParameter();
		fromParameter.ParameterName = "@fromId";
		fromParameter.Value = fromId;
		command.Parameters.Add(fromParameter);

		var toParameter = command.CreateParameter();
		toParameter.ParameterName = "@toId";
		toParameter.Value = toId;
		command.Parameters.Add(toParameter);

		_ = await command.ExecuteNonQueryAsync();
	}

	protected override async Task MoveNodeAsync(DbConnection connection, long nodeId, long newParentId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "UPDATE job_node SET parent_id = @newParentId, row_version = row_version + 1 WHERE id = @nodeId;";

		var nodeParameter = command.CreateParameter();
		nodeParameter.ParameterName = "@nodeId";
		nodeParameter.Value = nodeId;
		command.Parameters.Add(nodeParameter);

		var newParentParameter = command.CreateParameter();
		newParentParameter.ParameterName = "@newParentId";
		newParentParameter.Value = newParentId;
		command.Parameters.Add(newParentParameter);

		_ = await command.ExecuteNonQueryAsync();
	}
}
