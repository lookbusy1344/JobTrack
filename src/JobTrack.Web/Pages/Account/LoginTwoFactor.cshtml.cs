namespace JobTrack.Web.Pages.Account;

using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using Application;
using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>
///     ADR 0037: the two-factor challenge step of sign-in, reached only after <see cref="LoginModel" />'s
///     password step returns <see cref="Microsoft.AspNetCore.Identity.SignInResult.RequiresTwoFactor" />. Uses the same generic
///     failure message as the password step for the same no-enumeration reason (spec §7.1, threat model
///     row 2) — an invalid code and a missing/expired two-factor challenge look identical to the caller.
/// </summary>
public sealed class LoginTwoFactorModel(
	SignInManager<JobTrackIdentityUser> signInManager,
	UserManager<JobTrackIdentityUser> userManager,
	ILoginAttemptRateLimiter loginAttemptRateLimiter,
	IJobTrackClient jobTrackClient) : PageModel
{
	private const string GenericFailureMessage = "The verification code is incorrect.";
	private const string RateLimitedMessage = "Too many sign-in attempts. Retry after the current window elapses.";

	[BindProperty] public LoginTwoFactorInput Input { get; set; } = new();

	public string? ErrorMessage { get; private set; }

	public async Task<IActionResult> OnGetAsync() =>
		await signInManager.GetTwoFactorAuthenticationUserAsync() is null ? RedirectToPage("Login") : Page();

	public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
	{
		var twoFactorUser = await signInManager.GetTwoFactorAuthenticationUserAsync();
		if (twoFactorUser is null) {
			return RedirectToPage("Login");
		}

		var remoteAddress = GetRemoteAddress();
		var rateLimitOutcome = await loginAttemptRateLimiter.TryAcquireAsync(
			GetTwoFactorPartitionKey(remoteAddress, twoFactorUser), GetTwoFactorBackstopKey(remoteAddress), HttpContext.RequestAborted);
		switch (rateLimitOutcome) {
			case RateLimitOutcome.Denied:
				Response.StatusCode = StatusCodes.Status429TooManyRequests;
				ErrorMessage = RateLimitedMessage;
				return Page();
			case RateLimitOutcome.StoreUnavailable:
				// Fail closed (plan §2.4), matching LoginModel: the shared counter store being down must
				// look identical to an ordinary incorrect code, never a distinguishable response.
				ErrorMessage = GenericFailureMessage;
				return Page();
			case RateLimitOutcome.Allowed:
				break;
			default:
				throw new UnreachableException($"Unknown rate-limit outcome: {rateLimitOutcome}.");
		}

		if (!ModelState.IsValid) {
			return Page();
		}

		var result = await signInManager.TwoFactorAuthenticatorSignInAsync(Input.Code, false, false);
		if (!result.Succeeded) {
			if (await userManager.IsLockedOutAsync(twoFactorUser)) {
				await AuthenticationAudit.RecordKnownAsync(jobTrackClient, twoFactorUser, AuthenticationAuditEventKind.Lockout);
			} else {
				await AuthenticationAudit.RecordKnownAsync(jobTrackClient, twoFactorUser, AuthenticationAuditEventKind.TwoFactorFailed);
			}

			ErrorMessage = GenericFailureMessage;
			return Page();
		}

		await AuthenticationAudit.RecordKnownAsync(jobTrackClient, twoFactorUser, AuthenticationAuditEventKind.LoginSuccess);

		// §2.5 of the 2026-07-28 fresh-eyes review: this completes final authentication -- see
		// LoginModel's identical reset for why it happens here rather than after the password step.
		PrincipalBoundSessionState.Reset(HttpContext);

		if (twoFactorUser.RequiresPasswordChange) {
			return RedirectToPage("ChangePassword");
		}

		return returnUrl is not null && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage("/Index");
	}

	private static string GetTwoFactorPartitionKey(string remoteAddress, JobTrackIdentityUser twoFactorUser)
	{
		var normalizedUserName = twoFactorUser.NormalizedUserName
								 ?? twoFactorUser.UserName?.Trim().ToUpperInvariant()
								 ?? twoFactorUser.Id.ToString(CultureInfo.InvariantCulture);
		return $"two-factor:{remoteAddress}:{normalizedUserName}";
	}

	private static string GetTwoFactorBackstopKey(string remoteAddress) => $"two-factor:{remoteAddress}";

	private string GetRemoteAddress() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";

	public sealed class LoginTwoFactorInput
	{
		[Required] public string Code { get; init; } = string.Empty;
	}
}
