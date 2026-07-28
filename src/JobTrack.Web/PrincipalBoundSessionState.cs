namespace JobTrack.Web;

/// <summary>
///     §2.5 of the 2026-07-28 fresh-eyes review: the one reset boundary every principal-bound piece of
///     server-side session state (currently <see cref="FilterMemory" />'s remembered page filters) must
///     cross, so a future remembered value can never miss it by being added to a page without knowing
///     about the others. Call <see cref="Reset" /> exactly where a principal's identity changes:
///     successful final authentication (password-only sign-in, and the second step of a two-factor
///     sign-in) and logout -- never between the password and two-factor steps, since the principal has
///     not yet changed at that point.
/// </summary>
internal static class PrincipalBoundSessionState
{
	internal static void Reset(HttpContext context) => context.Session.Clear();
}
