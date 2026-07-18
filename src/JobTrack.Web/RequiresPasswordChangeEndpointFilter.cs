namespace JobTrack.Web;

using Identity;
using Microsoft.AspNetCore.Identity;

/// <summary>
///     Rejects authenticated <c>/api/*</c> requests when <see cref="JobTrackIdentityUser.RequiresPasswordChange" />
///     is set — spec §7.1 / §8.1: choose a new password before anything else is available. Mirrors
///     <see cref="RequiresPasswordChangePageFilter" /> for the external HTTP API surface.
/// </summary>
public sealed class RequiresPasswordChangeEndpointFilter(UserManager<JobTrackIdentityUser> userManager) : IEndpointFilter
{
	/// <summary>Stable problem <c>type</c> for API responses when password change is mandatory.</summary>
	public const string PasswordChangeRequiredProblemType = "/problems/password-change-required";

	public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
	{
		if (context.HttpContext.User.Identity?.IsAuthenticated == true) {
			var user = await userManager.GetUserAsync(context.HttpContext.User).ConfigureAwait(false);
			if (user is { RequiresPasswordChange: true }) {
				return TypedResults.Problem(
					"Choose a new password before using the API.",
					statusCode: StatusCodes.Status403Forbidden,
					title: "Password change required",
					type: PasswordChangeRequiredProblemType);
			}
		}

		return await next(context).ConfigureAwait(false);
	}
}
