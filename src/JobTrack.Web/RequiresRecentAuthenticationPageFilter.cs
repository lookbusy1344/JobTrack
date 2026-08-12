namespace JobTrack.Web;

using System.Reflection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NodaTime;

/// <summary>
///     ADR 0057 (§2.2): redirects to <c>/Account/ConfirmAccess</c> instead of invoking a handler method
///     carrying <see cref="RequiresRecentAuthenticationAttribute" /> unless the session's
///     recent-authentication timestamp (<see cref="SessionAuthenticationInstants" />) is within
///     <see cref="RecentAuthenticationWindow" />. Registered globally the same way as
///     <see cref="RequiresPasswordChangePageFilter" /> so a new sensitive handler only has to declare
///     the attribute, not wire up its own gate. The returned path drops any query string (notably
///     <c>?handler=...</c>) — the redirect target is always the page's plain GET, which the user
///     resubmits after confirming; POST-only form data is not preserved, the same cost every other
///     step-up UX in this codebase already accepts.
/// </summary>
public sealed class RequiresRecentAuthenticationPageFilter : IAsyncPageFilter
{
	public static readonly Duration RecentAuthenticationWindow = Duration.FromMinutes(15);

	public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

	public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
	{
		var httpContext = context.HttpContext;
		var requiresRecentAuthentication = context.HandlerMethod?.MethodInfo.GetCustomAttribute<RequiresRecentAuthenticationAttribute>() is not null;

		if (requiresRecentAuthentication && httpContext.User.Identity?.IsAuthenticated == true) {
			var authenticateResult = await httpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
			var recent = SessionAuthenticationInstants.TryGetRecentAuthentication(authenticateResult.Properties);
			var clock = httpContext.RequestServices.GetRequiredService<IClock>();

			if (recent is null || clock.GetCurrentInstant() - recent.Value > RecentAuthenticationWindow) {
				context.Result = new RedirectToPageResult("/Account/ConfirmAccess", new
				{
					returnUrl = httpContext.Request.Path.Value,
				});
				return;
			}
		}

		_ = await next();
	}
}
