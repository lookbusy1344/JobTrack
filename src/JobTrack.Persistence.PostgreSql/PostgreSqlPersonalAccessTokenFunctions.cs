namespace JobTrack.Persistence.PostgreSql;

using Abstractions;
using Microsoft.EntityFrameworkCore;
using NodaTime;

/// <summary>
///     Calls the SECURITY DEFINER <c>pat_*</c> PostgreSQL functions (security review remediation
///     §2.6, <c>database/postgresql/functions/jobtrack-security-definer-functions.sql</c>) that are
///     now <c>personal_access_token</c>'s only access path for the runtime <c>jobtrack_domain</c>
///     role -- it has no direct <c>SELECT</c>/<c>INSERT</c>/<c>UPDATE</c> grant on the table itself.
///     Every call runs on the caller's already-open <see cref="PostgreSqlJobTrackDbContext" />/
///     transaction, so it participates in the same single-commit unit of work as the rest of the
///     port's mutation and its <c>AuditEventWriter</c> row.
/// </summary>
internal static class PostgreSqlPersonalAccessTokenFunctions
{
	public static async Task<PersonalAccessTokenId> IssueAsync(
		PostgreSqlJobTrackDbContext context,
		AppUserId appUserId,
		string tokenHash,
		string label,
		Instant createdAt,
		Instant expiresAt,
		CancellationToken cancellationToken)
	{
		var id = await context.Database
			.SqlQuery<long>($"SELECT pat_issue({appUserId.Value}, {tokenHash}, {label}, {createdAt}, {expiresAt}) AS \"Value\"")
			.SingleAsync(cancellationToken).ConfigureAwait(false);

		return new(id);
	}

	public static async Task<PatAuthenticationRow?> TryAuthenticateAsync(
		PostgreSqlJobTrackDbContext context, string tokenHash, Instant now, CancellationToken cancellationToken) =>
		await context.Database
			.SqlQuery<PatAuthenticationRow>(
				$"SELECT id AS \"Id\", app_user_id AS \"AppUserId\" FROM pat_try_authenticate({tokenHash}, {now})")
			.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

	public static async Task<IReadOnlyList<PatSummaryRow>> ListAsync(
		PostgreSqlJobTrackDbContext context, AppUserId appUserId, CancellationToken cancellationToken) =>
		await context.Database
			.SqlQuery<PatSummaryRow>(
				$"""
				 SELECT id AS "Id", label AS "Label", created_at AS "CreatedAt", expires_at AS "ExpiresAt",
				        revoked_at AS "RevokedAt", last_used_at AS "LastUsedAt"
				 FROM pat_list({appUserId.Value})
				 """)
			.ToListAsync(cancellationToken).ConfigureAwait(false);

	public static async Task<PatRevokeRow> RevokeAsync(
		PostgreSqlJobTrackDbContext context,
		PersonalAccessTokenId tokenId,
		AppUserId appUserId,
		Instant now,
		CancellationToken cancellationToken) =>
		await context.Database
			.SqlQuery<PatRevokeRow>(
				$"SELECT found AS \"Found\", newly_revoked AS \"NewlyRevoked\" FROM pat_revoke({tokenId.Value}, {appUserId.Value}, {now})")
			.SingleAsync(cancellationToken).ConfigureAwait(false);

	/// <summary>Marks every currently-unrevoked token owned by <paramref name="appUserId" /> as revoked at <paramref name="now" />.</summary>
	public static async Task<int> RevokeAllForUserAsync(
		PostgreSqlJobTrackDbContext context, AppUserId appUserId, Instant now, CancellationToken cancellationToken) =>
		await context.Database.SqlQuery<int>($"SELECT pat_revoke_all({appUserId.Value}, {now}) AS \"Value\"")
			.SingleAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>One row of <see cref="PostgreSqlPersonalAccessTokenFunctions.TryAuthenticateAsync" />.</summary>
internal sealed record PatAuthenticationRow(long Id, long AppUserId);

/// <summary>One row of <see cref="PostgreSqlPersonalAccessTokenFunctions.ListAsync" />.</summary>
internal sealed record PatSummaryRow(long Id, string Label, Instant CreatedAt, Instant ExpiresAt, Instant? RevokedAt, Instant? LastUsedAt);

/// <summary>
///     The result of <see cref="PostgreSqlPersonalAccessTokenFunctions.RevokeAsync" />: <see cref="Found" />
///     is <see langword="false" /> when no token with that id exists for that user;
///     <see cref="NewlyRevoked" /> is <see langword="false" /> when the token was already revoked.
/// </summary>
internal sealed record PatRevokeRow(bool Found, bool NewlyRevoked);
