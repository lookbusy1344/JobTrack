namespace JobTrack.Persistence.Shared;

using Abstractions;
using Entities;
using NodaTime;

/// <summary>Applies authoritative credential-account state before stored roles are trusted.</summary>
internal static class ActorAccountState
{
	public static void EnsureMayAct(IdentityUserEntity identityUser, AppUserId actorId, Instant now)
	{
		EnsureMayAct(identityUser.IsEnabled, identityUser.LockoutEnabled, identityUser.LockoutEnd, actorId, now);
	}

	public static void EnsureMayAct(bool isEnabled, bool lockoutEnabled, Instant? lockoutEnd, AppUserId actorId, Instant now)
	{
		if (!isEnabled
			|| (lockoutEnabled && lockoutEnd is Instant lockedUntil && lockedUntil > now)) {
			throw new AuthorizationDeniedException($"Actor {actorId} has a disabled or locked account.");
		}
	}
}
