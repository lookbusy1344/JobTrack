namespace JobTrack.Persistence.Shared;

using Abstractions;
using Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

/// <summary>Applies authoritative credential-account state before stored roles are trusted.</summary>
internal static class ActorAccountState
{
	public static async Task<EquatableArray<EmployeeRole>> LoadRolesAsync(
		DbContext context, AppUserId actorId, Instant now, CancellationToken cancellationToken)
	{
		var identityUser = await context.Set<IdentityUserEntity>().AsNoTracking()
										.FirstOrDefaultAsync(user => user.AppUserId == actorId, cancellationToken).ConfigureAwait(false)
						   ?? throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		EnsureMayAct(identityUser, actorId, now);

		var roles = await context.Set<IdentityUserRoleEntity>().AsNoTracking()
								 .Where(userRole => userRole.IdentityUserId == identityUser.Id)
								 .Select(userRole => (EmployeeRole)userRole.IdentityRoleId)
								 .ToArrayAsync(cancellationToken).ConfigureAwait(false);

		return [.. roles];
	}

	public static void EnsureMayAct(IdentityUserEntity identityUser, AppUserId actorId, Instant now) =>
		EnsureMayAct(identityUser.IsEnabled, identityUser.LockoutEnabled, identityUser.LockoutEnd, actorId, now);

	public static void EnsureMayAct(bool isEnabled, bool lockoutEnabled, Instant? lockoutEnd, AppUserId actorId, Instant now)
	{
		if (!isEnabled
			|| lockoutEnabled && lockoutEnd is Instant lockedUntil && lockedUntil > now) {
			throw new AuthorizationDeniedException($"Actor {actorId} has a disabled or locked account.");
		}
	}
}
