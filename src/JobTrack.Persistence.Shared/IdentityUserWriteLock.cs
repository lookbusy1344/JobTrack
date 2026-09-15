namespace JobTrack.Persistence.Shared;

using Abstractions;
using Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Ports;

/// <summary>Serializes a compound write with concurrent credential-account state changes.</summary>
internal static class IdentityUserWriteLock
{
	/// <summary>Acquires every named identity row in stable id order, preventing cross-import lock inversion.</summary>
	public static async Task AcquireManyAsync(
		DbContext context, IProviderWriteOperations provider, IEnumerable<AppUserId> appUserIds, CancellationToken cancellationToken)
	{
		foreach (var appUserId in appUserIds.Distinct().OrderBy(id => id.Value)) {
			_ = await AcquireAsync(context, provider, appUserId, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	///     Locks the employee row and returns the credential-account facts for <paramref name="appUserId" />. The self-assignment is
	///     intentionally one EF-authored conditional update: PostgreSQL takes the row lock, while
	///     SQLite's enclosing <c>BEGIN IMMEDIATE</c> transaction already owns the provider write lock. The
	///     <c>identity_user</c> side goes through <paramref name="provider" /> rather than a direct EF
	///     update -- see <see cref="IProviderWriteOperations.TouchIdentityUserConcurrencyStampAsync" />.
	/// </summary>
	public static async Task<IdentityUserAccountState> AcquireAsync(
		DbContext context, IProviderWriteOperations provider, AppUserId appUserId, CancellationToken cancellationToken)
	{
		var affected = await context.Set<AppUserEntity>()
									.Where(user => user.Id == appUserId)
									.ExecuteUpdateAsync(
										setters => setters.SetProperty(user => user.RowVersion, user => user.RowVersion),
										cancellationToken)
									.ConfigureAwait(false);
		if (affected == 0) {
			throw new EntityNotFoundException($"Employee {appUserId} does not exist.");
		}

		await provider.TouchIdentityUserConcurrencyStampAsync(context, appUserId, cancellationToken).ConfigureAwait(false);

		return await context.Set<IdentityUserEntity>().AsNoTracking()
							.Where(identityUser => identityUser.AppUserId == appUserId)
							.Select(identityUser => new IdentityUserAccountState(
								identityUser.Id, identityUser.IsEnabled, identityUser.LockoutEnabled, identityUser.LockoutEnd))
							.SingleAsync(cancellationToken).ConfigureAwait(false);
	}
}

internal sealed record IdentityUserAccountState(long Id, bool IsEnabled, bool LockoutEnabled, Instant? LockoutEnd);
