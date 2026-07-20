namespace JobTrack.Web.Pages.Account;

using System.ComponentModel.DataAnnotations;
using Application;
using Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>
///     §8.5 slice 1: sign-in. The failure message is identical for an unknown username, a wrong
///     password, and a locked-out account — spec §7.1: "generic login failure messages shall not
///     reveal whether an employee account exists" (threat model row 2).
/// </summary>
[IgnoreAntiforgeryToken] // OnPostAsync validates the token itself so a stale one becomes a graceful redirect, not a 400.
public sealed class LoginModel(
	SignInManager<JobTrackIdentityUser> signInManager,
	UserManager<JobTrackIdentityUser> userManager,
	LoginAttemptRateLimiter loginAttemptRateLimiter,
	IJobTrackClient jobTrackClient,
	IAntiforgery antiforgery) : PageModel
{
	private const string GenericFailureMessage = "The username or password is incorrect.";
	private const string RateLimitedMessage = "Too many sign-in attempts. Retry after the current window elapses.";
	private const string SessionExpiredMessage = "Your session expired before sign-in completed. Please try again.";

	[BindProperty] public LoginInput Input { get; set; } = new();

	[TempData] public string? ExpiredNotice { get; set; }

	public string? ErrorMessage { get; private set; }

	public IActionResult OnGet(string? returnUrl = null)
	{
		// An already-authenticated visitor landing here -- a password manager re-opening the saved
		// login URL, or a SameSite bounce -- must be sent into the app rather than shown a live login
		// form. A re-shown form is what password managers auto-resubmit, and that second POST carries a
		// stale antiforgery token, producing the zero-byte 400 dead end this page must avoid.
		if (User.Identity?.IsAuthenticated == true) {
			return RedirectToApp(returnUrl);
		}

		ErrorMessage = ExpiredNotice;
		return Page();
	}

	public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
	{
		// Validate antiforgery in-handler (the class opts out of automatic validation). A stale or
		// missing token -- typically a scaled-to-zero cold start rotating the Data Protection keys
		// between form render and submit -- redirects back to a fresh login form instead of the
		// framework's zero-byte 400, which browsers silently replay on refresh. No credentials are
		// examined here, so a forged cross-site login POST is still rejected without authenticating.
		if (!await antiforgery.IsRequestValidAsync(HttpContext)) {
			ExpiredNotice = SessionExpiredMessage;
			return RedirectToPage(new { returnUrl });
		}

		var remoteAddress = GetRemoteAddress();
		if (!loginAttemptRateLimiter.TryAcquire(GetPasswordPartitionKey(remoteAddress, Input.UserName), GetPasswordBackstopKey(remoteAddress))) {
			Response.StatusCode = StatusCodes.Status429TooManyRequests;
			ErrorMessage = RateLimitedMessage;
			return Page();
		}

		if (!ModelState.IsValid) {
			return Page();
		}

		var result = await signInManager.PasswordSignInAsync(Input.UserName, Input.Password, false, true);
		var user = await userManager.FindByNameAsync(Input.UserName);

		if (result.RequiresTwoFactor) {
			return RedirectToPage("LoginTwoFactor", new { returnUrl });
		}

		if (!result.Succeeded) {
			if (user is not null) {
				if (await userManager.IsLockedOutAsync(user)) {
					await AuthenticationAudit.RecordKnownAsync(jobTrackClient, user, AuthenticationAuditEventKind.Lockout);
				} else {
					await AuthenticationAudit.RecordKnownAsync(jobTrackClient, user, AuthenticationAuditEventKind.LoginFailed);
				}
			} else {
				await AuthenticationAudit.RecordUnknownLoginFailedAsync(jobTrackClient);
			}

			ErrorMessage = GenericFailureMessage;
			return Page();
		}

		if (user is not null) {
			await AuthenticationAudit.RecordKnownAsync(jobTrackClient, user, AuthenticationAuditEventKind.LoginSuccess);
		}

		if (user is { RequiresPasswordChange: true }) {
			return RedirectToPage("ChangePassword");
		}

		return RedirectToApp(returnUrl);
	}

	private IActionResult RedirectToApp(string? returnUrl) =>
		returnUrl is not null && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage("/Index");

	private static string GetPasswordPartitionKey(string remoteAddress, string? userName)
	{
		var normalizedUserName = (userName ?? string.Empty).Trim().ToUpperInvariant();
		return $"password:{remoteAddress}:{normalizedUserName}";
	}

	private static string GetPasswordBackstopKey(string remoteAddress) => $"password:{remoteAddress}";

	private string GetRemoteAddress() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";

	public sealed class LoginInput
	{
		[Required] public string UserName { get; init; } = string.Empty;

		[Required] public string Password { get; init; } = string.Empty;
	}
}
