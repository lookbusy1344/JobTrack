namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlJobPrerequisiteSchemaTests()
	: JobPrerequisiteSchemaContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override object EncodeInstant(DateTimeOffset value) => value;

	protected override async Task AddPrerequisiteAsync(DbConnection connection, long fromId, long toId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT add_job_prerequisite(@fromId, @toId);";

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
		// Every node this schema-contract suite moves is freshly inserted and moved at most once,
		// so its row_version is always the schema default of 1.
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT move_job_node(@nodeId, @newParentId, @expectedVersion);";

		var nodeParameter = command.CreateParameter();
		nodeParameter.ParameterName = "@nodeId";
		nodeParameter.Value = nodeId;
		command.Parameters.Add(nodeParameter);

		var newParentParameter = command.CreateParameter();
		newParentParameter.ParameterName = "@newParentId";
		newParentParameter.Value = newParentId;
		command.Parameters.Add(newParentParameter);

		var expectedVersionParameter = command.CreateParameter();
		expectedVersionParameter.ParameterName = "@expectedVersion";
		expectedVersionParameter.Value = 1L;
		command.Parameters.Add(expectedVersionParameter);

		_ = await command.ExecuteNonQueryAsync();
	}
}
