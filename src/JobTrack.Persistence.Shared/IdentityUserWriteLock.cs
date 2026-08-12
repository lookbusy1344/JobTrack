namespace JobTrack.Persistence.Shared;

using Abstractions;
using Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>Serializes a compound write with concurrent credential-account state changes.</summary>
internal static class IdentityUserWriteLock
{
	/// <summary>Acquires every named identity row in stable id order, preventing cross-import lock inversion.</summary>
	public static async Task AcquireManyAsync(
		DbContext context, IEnumerable<AppUserId> appUserIds, CancellationToken cancellationToken)
	{
		foreach (var appUserId in appUserIds.Distinct().OrderBy(id => id.Value)) {
			_ = await AcquireAsync(context, appUserId, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	///     Locks and returns the identity row for <paramref name="appUserId" />. The self-assignment is
	///     intentionally one EF-authored conditional update: PostgreSQL takes the row lock, while
	///     SQLite's enclosing <c>BEGIN IMMEDIATE</c> transaction already owns the provider write lock.
	/// </summary>
	public static async Task<IdentityUserEntity> AcquireAsync(
		DbContext context, AppUserId appUserId, CancellationToken cancellationToken)
	{
		var affected = await context.Set<IdentityUserEntity>()
									.Where(identityUser => identityUser.AppUserId == appUserId)
									.ExecuteUpdateAsync(
										setters => setters.SetProperty(identityUser => identityUser.ConcurrencyStamp, identityUser => identityUser.ConcurrencyStamp),
										cancellationToken)
									.ConfigureAwait(false);
		if (affected == 0) {
			throw new EntityNotFoundException($"Employee {appUserId} does not exist.");
		}

		return await context.Set<IdentityUserEntity>().AsNoTracking()
							.SingleAsync(identityUser => identityUser.AppUserId == appUserId, cancellationToken).ConfigureAwait(false);
	}
}
