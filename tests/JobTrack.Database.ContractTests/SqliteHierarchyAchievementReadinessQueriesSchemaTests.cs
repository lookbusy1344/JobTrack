namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using TestSupport;

/// <summary>
///     SQLite has no user-defined functions, so each PostgreSQL stored function
///     from the sibling schema-version script becomes a minimal parameterized
///     statement issued directly here (impl plan §6.5: "SQLite provides the
///     equivalent behaviour through its enforcement triggers and minimal
///     parameterized statements, returning identical typed results"). Readiness
///     and the unsatisfied-prerequisite diagnostic are composed from
///     <see cref="NodeSucceededAsync" /> and <see cref="AncestorsAsync" /> via
///     separate round trips rather than one nested recursive statement, since
///     SQLite has no equivalent of PostgreSQL's composable table functions to
///     call from within another query.
/// </summary>
public sealed class SqliteHierarchyAchievementReadinessQueriesSchemaTests()
	: HierarchyAchievementReadinessQueriesSchemaContractTestsBase(new SqliteDatabaseFixture())
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

	protected override async Task<bool> NodeSucceededAsync(DbConnection connection, long nodeId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  WITH RECURSIVE subtree(id) AS (
							      SELECT @nodeId
							      UNION ALL
							      SELECT jn.id FROM job_node jn JOIN subtree s ON jn.parent_id = s.id
							  )
							  SELECT NOT EXISTS (
							      SELECT 1
							      FROM subtree s
							      WHERE NOT EXISTS (SELECT 1 FROM job_node c WHERE c.parent_id = s.id)
							        AND NOT EXISTS (
							            SELECT 1
							            FROM leaf_work lw
							            JOIN achievement_status a ON a.id = lw.achievement_id
							            WHERE lw.job_node_id = s.id AND a.name = 'Success'
							        )
							  );
							  """;
		AddParameter(command, "@nodeId", nodeId);
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != 0;
	}

	protected override async Task<IReadOnlyList<long>> AncestorsAsync(DbConnection connection, long nodeId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  WITH RECURSIVE ancestry(id) AS (
							      SELECT parent_id FROM job_node WHERE id = @nodeId
							      UNION ALL
							      SELECT jn.parent_id FROM job_node jn JOIN ancestry a ON jn.id = a.id WHERE jn.parent_id IS NOT NULL
							  )
							  SELECT id FROM ancestry WHERE id IS NOT NULL;
							  """;
		AddParameter(command, "@nodeId", nodeId);
		return await ReadLongColumnAsync(command);
	}

	protected override async Task<IReadOnlyList<long>> DescendantsAsync(DbConnection connection, long nodeId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  WITH RECURSIVE subtree(id) AS (
							      SELECT id FROM job_node WHERE parent_id = @nodeId
							      UNION ALL
							      SELECT jn.id FROM job_node jn JOIN subtree s ON jn.parent_id = s.id
							  )
							  SELECT id FROM subtree;
							  """;
		AddParameter(command, "@nodeId", nodeId);
		return await ReadLongColumnAsync(command);
	}

	protected override async Task<IReadOnlyList<long>> ControlledLeafIdsAsync(
		DbConnection connection, long actorId, IReadOnlyList<long> leafIds)
	{
		await using var command = connection.CreateCommand();
		var leafIdParameters = leafIds.Select((_, index) => $"@leafId{index}").ToArray();
		command.CommandText = $"""
							   WITH RECURSIVE ancestors(origin_leaf_id, id, owner_user_id, parent_id) AS (
							       SELECT id, id, owner_user_id, parent_id FROM job_node WHERE id IN ({string.Join(',', leafIdParameters)})
							       UNION ALL
							       SELECT a.origin_leaf_id, jn.id, jn.owner_user_id, jn.parent_id
							       FROM job_node jn JOIN ancestors a ON jn.id = a.parent_id
							   )
							   SELECT DISTINCT origin_leaf_id FROM ancestors WHERE owner_user_id = @actorId;
							   """;
		AddParameter(command, "@actorId", actorId);
		for (var index = 0; index < leafIds.Count; index++) {
			AddParameter(command, leafIdParameters[index], leafIds[index]);
		}

		return await ReadLongColumnAsync(command);
	}

	protected override async Task<bool> NodeReadyAsync(DbConnection connection, long nodeId) =>
		(await UnsatisfiedPrerequisitesAsync(connection, nodeId)).Count == 0;

	protected override async Task<IReadOnlyList<UnsatisfiedPrerequisite>> UnsatisfiedPrerequisitesAsync(DbConnection connection, long nodeId)
	{
		var declaringNodeIds = new List<long> { nodeId };
		declaringNodeIds.AddRange(await AncestorsAsync(connection, nodeId));

		var results = new List<UnsatisfiedPrerequisite>();

		foreach (var declaringNodeId in declaringNodeIds) {
			foreach (var requiredJobId in await RequiredJobIdsAsync(connection, declaringNodeId)) {
				if (!await NodeSucceededAsync(connection, requiredJobId)) {
					results.Add(new(declaringNodeId, requiredJobId));
				}
			}
		}

		return results;
	}

	private static async Task<IReadOnlyList<long>> RequiredJobIdsAsync(DbConnection connection, long declaredAtNodeId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT from_id FROM job_prerequisite WHERE to_id = @toId;";
		AddParameter(command, "@toId", declaredAtNodeId);
		return await ReadLongColumnAsync(command);
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
