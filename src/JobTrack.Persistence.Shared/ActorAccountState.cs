namespace JobTrack.Persistence.Shared;

using Abstractions;
using Entities;
using NodaTime;

/// <summary>Applies authoritative credential-account state before stored roles are trusted.</summary>
internal static class ActorAccountState
{
	public static void EnsureMayAct(IdentityUserEntity identityUser, AppUserId actorId, Instant now)
	{
		if (!identityUser.IsEnabled
			|| (identityUser.LockoutEnabled && identityUser.LockoutEnd is { } lockoutEnd && lockoutEnd > now)) {
			throw new AuthorizationDeniedException($"Actor {actorId} has a disabled or locked account.");
		}
	}
}
