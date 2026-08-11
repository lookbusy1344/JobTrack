namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TestSupport;

public sealed partial class SqliteJobNodeDependentTableCoverageTests()
	: JobNodeDependentTableCoverageTestsBase(new SqliteDatabaseFixture())
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

	/// <summary>
	///     SQLite has no catalogue view of foreign keys, only the per-table <c>foreign_key_list</c>
	///     pragma, so this walks every user table and keeps the rows pointing at
	///     <paramref name="targetTable" />. <c>pragma_foreign_key_list</c>'s table-valued form lets that
	///     walk stay one query.
	/// </summary>
	protected override async Task<IReadOnlyList<string>> ReadForeignKeysTargetingAsync(
		DbConnection connection, string targetTable, string? deleteRule = null)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT m.name || '.' || fk."from"
							  FROM sqlite_master m
							  	JOIN pragma_foreign_key_list(m.name) fk
							  WHERE m.type = 'table'
							  	AND fk."table" = @targetTable
							  	AND (@deleteRule IS NULL OR fk.on_delete = @deleteRule);
							  """;
		AddParameter(command, "@targetTable", targetTable);
		AddParameter(command, "@deleteRule", (object?)deleteRule ?? DBNull.Value);

		return await ReadStringsAsync(command);
	}

	/// <summary>
	///     <c>sqlite_master.sql</c> is the only record of a trigger's event — there is no catalogue
	///     column for it — so the <c>{BEFORE|AFTER|INSTEAD OF} DELETE ON</c> clause is matched on the
	///     statement text, across the arbitrary whitespace the schema files lay it out with. Matching
	///     the whole clause rather than a bare <c>DELETE</c> keeps a trigger whose <em>body</em> deletes
	///     rows from counting as one that fires on deletion. The table name still comes from the
	///     catalogue's own <c>tbl_name</c>, never from the text.
	/// </summary>
	protected override async Task<IReadOnlyList<string>> ReadDeleteTriggersAsync(
		DbConnection connection, IReadOnlyList<string> tables)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT tbl_name, sql FROM sqlite_master WHERE type = 'trigger';";

		var wanted = tables.ToHashSet(StringComparer.Ordinal);
		var triggerNames = new List<string>();

		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			var tableName = reader.GetString(0);
			var sql = reader.GetString(1);
			if (wanted.Contains(tableName) && DeleteTriggerClause().IsMatch(sql)) {
				triggerNames.Add($"{tableName}.{TriggerName().Match(sql).Groups["name"].Value}");
			}
		}

		return triggerNames;
	}

	[GeneratedRegex(@"\b(BEFORE|AFTER|INSTEAD\s+OF)\s+DELETE\s+ON\b", RegexOptions.IgnoreCase)]
	private static partial Regex DeleteTriggerClause();

	[GeneratedRegex(@"CREATE\s+TRIGGER\s+(?<name>\w+)", RegexOptions.IgnoreCase)]
	private static partial Regex TriggerName();

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
