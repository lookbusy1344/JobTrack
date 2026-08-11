namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlHierarchyMoveSchemaTests()
	: HierarchyMoveSchemaContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override object EncodeInstant(DateTimeOffset value) => value;

	protected override async Task MoveNodeAsync(DbConnection connection, long nodeId, long newParentId)
	{
		// Every node this schema-contract suite moves is freshly inserted and moved at most once,
		// so its row_version is always the schema default of 1 (impl plan §7.4's expected-version
		// CAS check, added in schema version 0016, is exercised with arbitrary versions by the
		// persistence-layer contract tests instead).
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
