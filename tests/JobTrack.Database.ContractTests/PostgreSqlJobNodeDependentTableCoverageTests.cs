namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlJobNodeDependentTableCoverageTests()
	: JobNodeDependentTableCoverageTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override async Task<IReadOnlyList<string>> ReadForeignKeysTargetingAsync(
		DbConnection connection, string targetTable, string? deleteRule = null)
	{
		// pg_constraint rather than information_schema: only the catalogue exposes the referencing
		// column list positionally, so a composite key would still be reported column by column.
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT c.conrelid::regclass::text || '.' || a.attname
							  FROM pg_constraint c
							  	CROSS JOIN LATERAL unnest(c.conkey) AS k(attnum)
							  	JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.attnum
							  WHERE c.contype = 'f'
							  	AND c.confrelid = @targetTable::regclass
							  	AND (@deleteRule::text IS NULL OR c.confdeltype = @deleteRule);
							  """;
		AddParameter(command, "@targetTable", targetTable);
		AddParameter(command, "@deleteRule", deleteRule is null ? DBNull.Value : DeleteRuleCode(deleteRule));

		return await ReadStringsAsync(command);
	}

	protected override async Task<IReadOnlyList<string>> ReadDeleteTriggersAsync(
		DbConnection connection, IReadOnlyList<string> tables)
	{
		// tgtype bit 3 (value 8) is the DELETE event; tgisinternal excludes the triggers PostgreSQL
		// creates to implement foreign keys, which are not application refusals.
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT t.tgrelid::regclass::text || '.' || t.tgname
							  FROM pg_trigger t
							  WHERE NOT t.tgisinternal
							  	AND (t.tgtype & 8) <> 0
							  	AND t.tgrelid::regclass::text = ANY (@tables);
							  """;
		AddParameter(command, "@tables", tables.ToArray());

		return await ReadStringsAsync(command);
	}

	/// <summary><c>pg_constraint.confdeltype</c>'s single-character encoding of a delete rule.</summary>
	private static string DeleteRuleCode(string deleteRule) => deleteRule switch {
		"RESTRICT" => "r",
		"SET NULL" => "n",
		"CASCADE" => "c",
		"NO ACTION" => "a",
		_ => throw new ArgumentOutOfRangeException(nameof(deleteRule), deleteRule, "Unknown foreign-key delete rule."),
	};

	private static async Task<IReadOnlyList<string>> ReadStringsAsync(DbCommand command)
	{
		var values = new List<string>();
		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			values.Add(reader.GetString(0));
		}

		return values;
	}

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}
