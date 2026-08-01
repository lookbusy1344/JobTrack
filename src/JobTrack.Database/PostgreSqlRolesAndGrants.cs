namespace JobTrack.Database;

using System.Data.Common;

/// <summary>
///     Applies a fixed, unversioned PostgreSQL script -- the roles-and-grants script (impl
///     plan §6.1) or the SECURITY DEFINER functions script (security review remediation §2.6) --
///     after schema deployment. Neither is a <see cref="SchemaVersionScript" />: they carry no
///     version number and are not recorded in <c>schema_version</c> (see each script's own header
///     comment for why). SQLite has no roles, GRANT, or SECURITY DEFINER concept, so there is no
///     equivalent for that provider.
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
