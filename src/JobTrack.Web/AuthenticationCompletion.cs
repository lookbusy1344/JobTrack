namespace JobTrack.Web;

using Application;
using Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>
///     The one final-authentication tail shared by password and passkey sign-in (ADR 0071 §8.2): record
///     the success audit, reset any principal-bound session state left by a previous principal on this
///     browser, and resolve the post-sign-in local target — the forced password-change page when the
///     administrator requires it (a passkey cannot bypass it while the password is still the recovery
///     credential), otherwise the validated <c>returnUrl</c>, otherwise Home. Routing both methods
///     through here means neither can omit the audit, the reset, or the forced-change gate.
/// </summary>
internal static class AuthenticationCompletion
{
	public static async Task<string> FinishAsync(
		PageModel page,
		IJobTrackClient jobTrackClient,
		JobTrackIdentityUser user,
		AuthenticationAuditEventKind successKind,
		string? returnUrl,
		CancellationToken cancellationToken = default)
	{
		await AuthenticationAudit.RecordKnownAsync(jobTrackClient, user, successKind, cancellationToken);
		PrincipalBoundSessionState.Reset(page.HttpContext);

		if (user.RequiresPasswordChange) {
			return page.Url.Page("/Account/ChangePassword") ?? "/Account/ChangePassword";
		}

		if (returnUrl is not null && page.Url.IsLocalUrl(returnUrl)) {
			return returnUrl;
		}

		return page.Url.Page("/Index") ?? "/";
	}
}
