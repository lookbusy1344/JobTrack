namespace JobTrack.Web;

using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using NodaTime;

/// <summary>
///     Reads and writes the two session timestamps ADR 0057 needs, both carried in
///     <see cref="AuthenticationProperties.Items" /> rather than as claims — the security stamp
///     validator (<c>SecurityStampValidatorOptions.ValidationInterval = TimeSpan.Zero</c>) rebuilds the
///     <see cref="System.Security.Claims.ClaimsPrincipal" /> from the claims factory on every request,
///     discarding any claim added outside it, but reuses the same <see cref="AuthenticationProperties" />
///     instance on its ticket-regeneration path, so <c>Items</c> survives.
/// </summary>
public static class SessionAuthenticationInstants
{
	/// <summary>Set once at sign-in; never advanced except by a fresh sign-in. The absolute-ceiling anchor.</summary>
	private const string OriginItemKey = "jt.origin";

	/// <summary>Set at sign-in and refreshed by step-up confirmation. The recent-authentication freshness anchor.</summary>
	private const string RecentItemKey = "jt.recent";

	/// <summary>
	///     Stamps <paramref name="properties" /> for a sign-in event: <paramref name="origin" /> is
	///     whatever the caller determines the session's origin should be (the existing session's origin,
	///     if there was one, otherwise <paramref name="now" />), and <c>recent</c> is always
	///     <paramref name="now" /> — every call site of this method represents an event that itself
	///     constitutes fresh authentication.
	/// </summary>
	public static void Stamp(AuthenticationProperties properties, Instant origin, Instant now)
	{
		properties.Items[OriginItemKey] = Serialize(origin);
		properties.Items[RecentItemKey] = Serialize(now);
	}

	public static Instant? TryGetOrigin(AuthenticationProperties? properties) => TryDeserialize(properties, OriginItemKey);

	public static Instant? TryGetRecentAuthentication(AuthenticationProperties? properties) => TryDeserialize(properties, RecentItemKey);

	private static string Serialize(Instant instant) => instant.ToUnixTimeTicks().ToString(CultureInfo.InvariantCulture);

	private static Instant? TryDeserialize(AuthenticationProperties? properties, string key)
	{
		if (properties is null || !properties.Items.TryGetValue(key, out var raw) || raw is null) {
			return null;
		}

		return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
			? Instant.FromUnixTimeTicks(ticks)
			: null;
	}
}
