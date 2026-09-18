namespace JobTrack.Web.Pages.Account;

using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Application;
using Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Passkeys;
using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;

/// <summary>
///     §8.5 slice 1: sign-in. The failure message is identical for an unknown username, a wrong
///     password, and a locked-out account — spec §7.1: "generic login failure messages shall not
///     reveal whether an employee account exists" (threat model row 2).
/// </summary>
[IgnoreAntiforgeryToken] // OnPostAsync validates the token itself so a stale one becomes a graceful redirect, not a 400.
public sealed class LoginModel(
	SignInManager<JobTrackIdentityUser> signInManager,
	UserManager<JobTrackIdentityUser> userManager,
	ILoginAttemptRateLimiter loginAttemptRateLimiter,
	IJobTrackClient jobTrackClient,
	IAntiforgery antiforgery,
	IOptions<PasskeyFeatureOptions> passkeyFeature) : PageModel
{
	private const string GenericFailureMessage = "The username or password is incorrect.";
	private const string GenericPasskeyFailureMessage = "That passkey could not be used to sign in. Try again.";
	private const string PasskeyExpiredMessage = "Passkey sign-in expired; try again.";
	private const string RateLimitedMessage = "Too many sign-in attempts. Retry after the current window elapses.";
	private const string SessionExpiredMessage = "Your session expired before sign-in completed. Please try again.";

	/// <summary>Whether passkey sign-in is enabled, so the page renders the progressive enhancement and the handlers accept requests.</summary>
	public bool PasskeysEnabled => passkeyFeature.Value.Enabled;

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
			return RedirectToPage(new
			{
				returnUrl,
			});
		}

		var remoteAddress = GetRemoteAddress();
		var rateLimitOutcome = await loginAttemptRateLimiter.TryAcquireAsync(
			GetPasswordPartitionKey(remoteAddress, Input.UserName), GetPasswordBackstopKey(remoteAddress), HttpContext.RequestAborted);
		switch (rateLimitOutcome) {
			case RateLimitOutcome.Denied:
				Response.StatusCode = StatusCodes.Status429TooManyRequests;
				ErrorMessage = RateLimitedMessage;
				return Page();
			case RateLimitOutcome.StoreUnavailable:
				// Fail closed (plan §2.4) without disclosing that anything unusual happened: the
				// shared counter store being down must look identical to an ordinary wrong-password
				// attempt, never a distinguishable response an attacker could use to detect it.
				// Sign-in is never attempted -- there is no rate-limit evidence either way.
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

		var result = await signInManager.PasswordSignInAsync(Input.UserName, Input.Password, false, true);
		var user = await userManager.FindByNameAsync(Input.UserName);

		if (result.RequiresTwoFactor) {
			return RedirectToPage("LoginTwoFactor", new
			{
				returnUrl,
			});
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

		if (user is null) {
			// PasswordSignInAsync succeeded, so the account exists; a null lookup here is not expected.
			// Reset principal-bound state and send them into the app without a success audit or a
			// forced-change gate we cannot evaluate.
			PrincipalBoundSessionState.Reset(HttpContext);
			return RedirectToApp(returnUrl);
		}

		// The one shared final-authentication tail (audit, principal-bound-session reset before any
		// redirect including the forced-password-change one, and the forced-change gate); password and
		// passkey sign-in both route through it.
		var target = await AuthenticationCompletion.FinishAsync(
			this, jobTrackClient, user, AuthenticationAuditEventKind.LoginSuccess, returnUrl, HttpContext.RequestAborted);
		return LocalRedirect(target);
	}

	/// <summary>
	///     Generates username-less WebAuthn request options for both conditional autofill and the
	///     explicit button (ADR 0071 §8.2). Antiforgery-protected and rate-limited before generation;
	///     never reveals whether any account has passkeys.
	/// </summary>
	public async Task<IActionResult> OnPostPasskeyOptionsAsync()
	{
		if (!PasskeysEnabled) {
			return NotFound();
		}

		if (!await antiforgery.IsRequestValidAsync(HttpContext)) {
			return new JsonResult(new
			{
				error = PasskeyExpiredMessage,
			});
		}

		var limited = await ConsumePasskeyLimitAsync("passkey-options");
		if (limited is not null) {
			return limited;
		}

		// Username-less: a null user yields an empty allowCredentials list, so the browser's own chooser
		// can offer any discoverable passkey without JobTrack disclosing which accounts have one.
		var optionsJson = await signInManager.MakePasskeyRequestOptionsAsync(null);
		return Content(optionsJson, "application/json");
	}

	/// <summary>
	///     Verifies a WebAuthn assertion and, on success, completes sign-in through the shared final-login
	///     tail (ADR 0071 §8.2). A user-verified passkey is sufficient — no TOTP follows. The authenticated
	///     user is resolved only after success, by decoding the credential ID from the same assertion JSON
	///     and looking it up; a pre-verification credential ID is never trusted. Returns JSON for
	///     <c>passkeys.js</c>.
	/// </summary>
	public async Task<IActionResult> OnPostPasskeySignInAsync(string? returnUrl = null)
	{
		if (!PasskeysEnabled) {
			return NotFound();
		}

		if (!await antiforgery.IsRequestValidAsync(HttpContext)) {
			return new JsonResult(new
			{
				error = PasskeyExpiredMessage,
			});
		}

		var limited = await ConsumePasskeyLimitAsync("passkey-assertion");
		if (limited is not null) {
			return limited;
		}

		var credentialJson = await PasskeyCredentialJson.ReadAsync(Request, HttpContext.RequestAborted);
		if (credentialJson is null) {
			return new JsonResult(new
			{
				error = GenericPasskeyFailureMessage,
			});
		}

		SignInResult result;
		try {
			result = await signInManager.PasskeySignInAsync(credentialJson);
		}
		catch (InvalidOperationException) {
			// Some .NET 10 servicing builds throw rather than returning Failed when the protected
			// assertion state is absent (an expected browser condition, e.g. an expired ceremony).
			// Translate only this ceremony call's exception into the generic expiry response.
			return new JsonResult(new
			{
				error = PasskeyExpiredMessage,
			});
		}
		catch (DbUpdateConcurrencyException) {
			// A reset/removal committed after assertion verification. The store refuses to recreate
			// that credential; expose the same generic authentication failure as any invalid assertion.
			return new JsonResult(new
			{
				error = GenericPasskeyFailureMessage,
			});
		}

		if (!result.Succeeded) {
			await AuthenticationAudit.RecordUnknownPasskeySignInFailedAsync(jobTrackClient);
			return new JsonResult(new
			{
				error = GenericPasskeyFailureMessage,
			});
		}

		// Resolve the authenticated user only now, from the verified assertion (§7.2).
		if (!PasskeyAssertionCredential.TryReadCredentialId(credentialJson, out var credentialId)) {
			await signInManager.SignOutAsync();
			return new JsonResult(new
			{
				error = GenericPasskeyFailureMessage,
			});
		}

		var user = await userManager.FindByPasskeyIdAsync(credentialId);
		if (user is null) {
			// A concurrent reset removed the credential between verification and lookup: revoke the
			// just-issued cookie and fail closed rather than complete a session we cannot attribute.
			await signInManager.SignOutAsync();
			return new JsonResult(new
			{
				error = GenericPasskeyFailureMessage,
			});
		}

		var target = await AuthenticationCompletion.FinishAsync(
			this, jobTrackClient, user, AuthenticationAuditEventKind.PasskeySignInSuccess, returnUrl, HttpContext.RequestAborted);
		return new JsonResult(new
		{
			redirect = target,
		});
	}

	private async Task<IActionResult?> ConsumePasskeyLimitAsync(string purpose)
	{
		var remoteAddress = GetRemoteAddress();
		var outcome = await loginAttemptRateLimiter.TryAcquireAsync(
			GetPasskeyPartitionKey(purpose, remoteAddress),
			GetPasskeyBackstopKey(purpose, remoteAddress),
			HttpContext.RequestAborted);
		return outcome switch {
			RateLimitOutcome.Allowed => null,
			RateLimitOutcome.Denied => StatusJson(StatusCodes.Status429TooManyRequests, RateLimitedMessage),
			// Fail closed without disclosing the store is down: a generic failure, no ceremony attempted.
			RateLimitOutcome.StoreUnavailable => new(new
			{
				error = GenericPasskeyFailureMessage,
			}),
			_ => throw new UnreachableException($"Unknown rate-limit outcome: {outcome}."),
		};
	}

	private JsonResult StatusJson(int statusCode, string error)
	{
		Response.StatusCode = statusCode;
		return new(new
		{
			error,
		});
	}

	private IActionResult RedirectToApp(string? returnUrl) =>
		returnUrl is not null && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage("/Index");

	private static string GetPasswordPartitionKey(string remoteAddress, string? userName)
	{
		var normalizedUserName = (userName ?? string.Empty).Trim().ToUpperInvariant();
		return $"password:{remoteAddress}:{normalizedUserName}";
	}

	private static string GetPasswordBackstopKey(string remoteAddress) => $"password:{remoteAddress}";

	private static string GetPasskeyPartitionKey(string purpose, string remoteAddress) => $"{purpose}:request:{remoteAddress}";

	private static string GetPasskeyBackstopKey(string purpose, string remoteAddress) => $"{purpose}:origin:{remoteAddress}";

	private string GetRemoteAddress() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";

	public sealed class LoginInput
	{
		[Required]
		[Display(Name = "User name")]
		public string UserName { get; init; } = string.Empty;

		[Required] public string Password { get; init; } = string.Empty;
	}
}
