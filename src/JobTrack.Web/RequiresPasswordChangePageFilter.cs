namespace JobTrack.Web;

using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

/// <summary>
///     Redirects a signed-in user with <see cref="JobTrackIdentityUser.RequiresPasswordChange" /> set
///     to the change-password page for every page except the ones that must remain reachable
///     (change-password itself, logout) — spec §7.1: the employee "shall" choose a new password at
///     next sign-in before anything else is available. Reloads authoritative state per request rather
///     than trusting a cookie-cached claim (mirrors plan §8.3's authorization principle).
/// </summary>
public sealed class RequiresPasswordChangePageFilter : IAsyncPageFilter
{
	private static readonly HashSet<string> ExemptPagePaths = new(StringComparer.OrdinalIgnoreCase) {
		"/Account/ChangePassword", "/Account/Logout", "/Account/Login", "/Account/AccessDenied",
	};

	public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

	public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
	{
		var httpContext = context.HttpContext;

		if (httpContext.User.Identity?.IsAuthenticated == true
			&& !ExemptPagePaths.Contains(context.ActionDescriptor.ViewEnginePath)) {
			var userManager = httpContext.RequestServices.GetRequiredService<UserManager<JobTrackIdentityUser>>();
			var user = await userManager.GetUserAsync(httpContext.User);

			if (user is { RequiresPasswordChange: true }) {
				context.Result = new RedirectToPageResult("/Account/ChangePassword");
				return;
			}
		}

		_ = await next();
	}
}
