namespace JobTrack.Database;

using System.Data.Common;

/// <summary>
///     Applies the fixed, unversioned PostgreSQL roles-and-grants script (impl
///     plan §6.1) after schema deployment. Not a <see cref="SchemaVersionScript" />:
///     it carries no version number and is not recorded in <c>schema_version</c>
///     (see the script's own header comment for why). SQLite has no roles or
///     GRANT concept, so there is no equivalent for that provider.
/// </summary>
public static class PostgreSqlRolesAndGrants
{
	public static async Task ApplyAsync(DbConnection connection, string scriptPath, CancellationToken cancellationToken)
	{
		var sql = await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);

		await using var command = connection.CreateCommand();
		command.CommandText = sql;
		_ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}
}
