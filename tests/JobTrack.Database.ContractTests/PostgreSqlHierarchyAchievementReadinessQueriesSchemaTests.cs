namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlHierarchyAchievementReadinessQueriesSchemaTests()
	: HierarchyAchievementReadinessQueriesSchemaContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override object EncodeInstant(DateTimeOffset value) => value;

	protected override async Task<bool> NodeSucceededAsync(DbConnection connection, long nodeId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT node_succeeded(@nodeId);";
		AddParameter(command, "@nodeId", nodeId);
		return (bool)(await command.ExecuteScalarAsync())!;
	}

	protected override async Task<IReadOnlyList<long>> AncestorsAsync(DbConnection connection, long nodeId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT id FROM job_node_ancestors(@nodeId);";
		AddParameter(command, "@nodeId", nodeId);
		return await ReadLongColumnAsync(command);
	}

	protected override async Task<IReadOnlyList<long>> DescendantsAsync(DbConnection connection, long nodeId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT id FROM job_node_descendants(@nodeId);";
		AddParameter(command, "@nodeId", nodeId);
		return await ReadLongColumnAsync(command);
	}

	protected override async Task<bool> NodeReadyAsync(DbConnection connection, long nodeId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT job_node_ready(@nodeId);";
		AddParameter(command, "@nodeId", nodeId);
		return (bool)(await command.ExecuteScalarAsync())!;
	}

	protected override async Task<IReadOnlyList<UnsatisfiedPrerequisite>> UnsatisfiedPrerequisitesAsync(DbConnection connection, long nodeId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT declared_at_node_id, required_job_id FROM job_node_unsatisfied_prerequisites(@nodeId);";
		AddParameter(command, "@nodeId", nodeId);

		var results = new List<UnsatisfiedPrerequisite>();
		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			results.Add(new(reader.GetInt64(0), reader.GetInt64(1)));
		}

		return results;
	}

	private static async Task<IReadOnlyList<long>> ReadLongColumnAsync(DbCommand command)
	{
		var results = new List<long>();
		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			results.Add(reader.GetInt64(0));
		}

		return results;
	}

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}
