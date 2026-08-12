namespace JobTrack.TestSupport;

using System.Data.Common;
using Database;

/// <summary>
///     Applies the roles-and-grants script and the SECURITY DEFINER <c>pat_*</c> functions script
///     (security review remediation §2.6) after schema deployment, for any provider-parameterized
///     contract test base whose PostgreSQL command ports now call those functions (currently
///     personal-access-token issue/authenticate/list/revoke and employee/account-credential command
///     ports' revoke-all-on-security-transition path) -- regardless of which role the test's own
///     connection runs as, the functions must exist for the port to call them at all. A no-op on
///     SQLite, which has no roles, GRANT, or SECURITY DEFINER concept.
/// </summary>
public static class PostgreSqlTestInfrastructure
{
	public static async Task EnsureSecurityDefinerFunctionsAsync(
		DbConnection connection, SchemaProvider provider, CancellationToken cancellationToken = default)
	{
		if (provider != SchemaProvider.PostgreSql) {
			return;
		}

		await PostgreSqlRolesAndGrants.ApplyAsync(connection, RepositoryPaths.PostgreSqlRolesAndGrantsScriptPath(), cancellationToken)
									  .ConfigureAwait(false);
		await PostgreSqlRolesAndGrants.ApplyAsync(connection, RepositoryPaths.PostgreSqlFunctionsScriptPath(), cancellationToken)
									  .ConfigureAwait(false);
	}
}
