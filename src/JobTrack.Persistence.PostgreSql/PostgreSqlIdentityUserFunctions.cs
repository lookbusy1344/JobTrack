namespace JobTrack.Persistence.PostgreSql;

using Abstractions;
using Microsoft.EntityFrameworkCore;

/// <summary>
///     Calls the SECURITY DEFINER <c>identity_user_touch_concurrency_stamp</c> PostgreSQL function
///     (<c>database/postgresql/functions/jobtrack-security-definer-functions.sql</c>) that stands in
///     for <see cref="Shared.IdentityUserWriteLock" />'s direct EF update on <c>identity_user</c> --
///     <c>jobtrack_domain</c> has no <c>UPDATE</c> grant on that table (see
///     <c>database/postgresql/roles/jobtrack-roles-and-grants.sql</c>). Runs on the caller's already-open
///     <see cref="DbContext" />/transaction, so it commits with the rest of the port's write.
/// </summary>
internal static class PostgreSqlIdentityUserFunctions
{
	public static async Task TouchConcurrencyStampAsync(DbContext context, AppUserId appUserId, CancellationToken cancellationToken) =>
		_ = await context.Database
						 .ExecuteSqlInterpolatedAsync(
							 $"SELECT identity_user_touch_concurrency_stamp({appUserId.Value})", cancellationToken)
						 .ConfigureAwait(false);
}
