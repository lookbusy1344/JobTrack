namespace JobTrack.Web;

/// <summary>
///     ADR 0057 (§2.2): marks a Razor Page handler method as a sensitive operation that a bare
///     authenticated cookie is not sufficient to reach — <see cref="RequiresRecentAuthenticationPageFilter" />
///     redirects to <c>/Account/ConfirmAccess</c> instead of invoking the handler unless
///     <see cref="SessionAuthenticationInstants.TryGetRecentAuthentication" /> is within the configured
///     freshness window.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresRecentAuthenticationAttribute : Attribute { }
