namespace JobTrack.Persistence.Shared;

using Abstractions;
using Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

/// <summary>
///     Revokes every live personal access token for a user (ADR 0029) from within an already-open
///     context/transaction. Both providers' <c>IEmployeeCommandPort</c> implementations call this at
///     every security-sensitive account transition that already rotates the security stamp
///     (disablement, password reset, role change), alongside the narrower single-token
///     <c>IPersonalAccessTokenPort.RevokeAsync</c>/<c>RevokeAllAsync</c> a caller can invoke directly.
/// </summary>
internal static class PersonalAccessTokenRevocation
{
	/// <summary>Marks every currently-unrevoked token owned by <paramref name="userId" /> as revoked at <paramref name="now" />.</summary>
	public static Task<int> RevokeAllForUserAsync(DbContext context, AppUserId userId, Instant now, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);

		return context.Set<PersonalAccessTokenEntity>()
			.Where(token => token.AppUserId == userId && token.RevokedAt == null)
			.ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now), cancellationToken);
	}
}
