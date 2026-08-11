namespace JobTrack.Web;

/// <summary>
///     §2.5 of the 2026-07-28 fresh-eyes review: the one reset boundary every principal-bound piece of
///     remembered state (currently <see cref="FilterMemory" />'s remembered page filters, held in
///     <see cref="CookieFilterMemoryStore" /> since ADR 0066 Stage 3) must cross, so a future
///     remembered value can never miss it by being added to a page without knowing about the others.
///     Call <see cref="Reset" /> exactly where a principal's identity changes: successful final
///     authentication (password-only sign-in, and the second step of a two-factor sign-in) and logout
///     -- never between the password and two-factor steps, since the principal has not yet changed at
///     that point.
/// </summary>
internal static class PrincipalBoundSessionState
{
	// Deleting the cookie outright (rather than reading, clearing, and re-persisting it through
	// CookieFilterMemoryStore) needs no data-protection round trip and works even when the
	// principal is changing -- exactly the moment a store bound to the *new* principal could not
	// have decrypted the *old* one's cookie anyway.
	internal static void Reset(HttpContext context) => context.Response.Cookies.Delete(CookieFilterMemoryStore.CookieName);
}
