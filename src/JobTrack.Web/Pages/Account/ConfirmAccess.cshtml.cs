namespace JobTrack.Web.Pages.Account;

using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using Application;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Passkeys;
using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;

/// <summary>
///     ADR 0057 (§2.2): the step-up confirmation every <see cref="RequiresRecentAuthenticationAttribute" />-marked
///     handler redirects to when the session's recent-authentication timestamp has gone stale. Re-collects
///     the current password (and, if the account has TOTP enabled, a fresh code) for the already
///     signed-in user, then refreshes the session's recent-authentication timestamp via
///     <see cref="SignInManager{TUser}.RefreshSignInAsync" /> -- <see cref="JobTrackSignInManager" />'s
///     override stamps that call's <c>recent</c> to now while leaving <c>origin</c> (the absolute session
///     ceiling) untouched. Password verification goes through <see cref="SignInManager{TUser}.CheckPasswordSignInAsync" />,
///     not a raw <see cref="UserManager{TUser}.CheckPasswordAsync" />, so repeated wrong guesses count
///     toward the account's existing lockout policy exactly as they would on the login page.
///     ADR 0071 §8.3: an owned, user-verified passkey is an alternative step-up. It refreshes only
///     <c>recent</c> too and needs no TOTP; the resolved user is checked to equal the signed-in principal
///     before the session is refreshed.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.AnyAuthenticatedUser)]
public sealed class ConfirmAccessModel(
	SignInManager<JobTrackIdentityUser> signInManager,
	UserManager<JobTrackIdentityUser> userManager,
	ILoginAttemptRateLimiter loginAttemptRateLimiter,
	IJobTrackClient jobTrackClient,
	IOptions<PasskeyFeatureOptions> passkeyFeature) : PageModel
{
	private const string RateLimitedMessage = "Too many authentication attempts. Retry after the current window elapses.";
	private const string GenericPasskeyFailureMessage = "That passkey could not be used. Try again.";
	private const string PasskeyExpiredMessage = "Passkey confirmation expired; try again.";

	[BindProperty] public ConfirmAccessInput Input { get; set; } = new();

	public bool RequiresTwoFactorCode { get; private set; }

	/// <summary>True when the feature is enabled and the signed-in user owns at least one passkey, so the page offers passkey step-up.</summary>
	public bool PasskeyStepUpAvailable { get; private set; }

	public string? ErrorMessage { get; private set; }

	public string? ReturnUrl { get; private set; }

	private bool PasskeysEnabled => passkeyFeature.Value.Enabled;

	public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
	{
		var user = await userManager.GetUserAsync(User);
		if (user is null) {
			return Challenge();
		}

		ReturnUrl = returnUrl;
		await LoadStateAsync(user);
		return Page();
	}

	public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
	{
		var user = await userManager.GetUserAsync(User);
		if (user is null) {
			return Challenge();
		}

		ReturnUrl = returnUrl;
		await LoadStateAsync(user);
		var remoteAddress = GetRemoteAddress();
		var rateLimitOutcome = await loginAttemptRateLimiter.TryAcquireAsync(
			GetPartitionKey(remoteAddress, user), GetBackstopKey(remoteAddress), HttpContext.RequestAborted);
		switch (rateLimitOutcome) {
			case RateLimitOutcome.Denied:
				Response.StatusCode = StatusCodes.Status429TooManyRequests;
				ErrorMessage = RateLimitedMessage;
				return Page();
			case RateLimitOutcome.StoreUnavailable:
				// Fail closed (plan §2.4), matching LoginModel/LoginTwoFactorModel: the shared counter
				// store being down must look identical to an ordinary wrong password, never a
				// distinguishable response.
				ErrorMessage = "That password is incorrect.";
				return Page();
			case RateLimitOutcome.Allowed:
				break;
			default:
				throw new UnreachableException($"Unknown rate-limit outcome: {rateLimitOutcome}.");
		}

		if (!ModelState.IsValid) {
			return Page();
		}

		var passwordCheck = await signInManager.CheckPasswordSignInAsync(user, Input.CurrentPassword, true);
		if (!passwordCheck.Succeeded) {
			ErrorMessage = passwordCheck.IsLockedOut
				? "This account is temporarily locked out after too many failed attempts."
				: "That password is incorrect.";
			return Page();
		}

		if (RequiresTwoFactorCode
			&& !await userManager.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, Input.TwoFactorCode ?? string.Empty)) {
			ErrorMessage = "That verification code is incorrect.";
			return Page();
		}

		await signInManager.RefreshSignInAsync(user);

		return RedirectToReturnUrl(returnUrl);
	}

	/// <summary>
	///     Generates WebAuthn request options restricted to the signed-in user (ADR 0071 §8.3), so only
	///     that user's own credentials can satisfy the step-up. Antiforgery-protected (automatic) and
	///     rate-limited before generation. Returns JSON for <c>passkeys.js</c>.
	/// </summary>
	public async Task<IActionResult> OnPostPasskeyOptionsAsync()
	{
		if (!PasskeysEnabled) {
			return NotFound();
		}

		var user = await userManager.GetUserAsync(User);
		if (user is null) {
			return Challenge();
		}

		var limited = await ConsumePasskeyLimitAsync("passkey-stepup-options", user);
		if (limited is not null) {
			return limited;
		}

		var optionsJson = await signInManager.MakePasskeyRequestOptionsAsync(user);
		return Content(optionsJson, "application/json");
	}

	/// <summary>
	///     Verifies a WebAuthn assertion for the signed-in user through the native
	///     <see cref="SignInManager{TUser}.PasskeySignInAsync" /> (ADR 0071 §8.3): the framework persists
	///     the refreshed sign counter and completes with TOTP bypass, and
	///     <see cref="JobTrackSignInManager.SignInWithClaimsAsync" /> preserves the session's absolute
	///     <c>origin</c> while restamping <c>recent</c> to now — so a user-verified passkey is a full
	///     step-up with no TOTP. The credential is resolved only after success (§7.2) and must belong to
	///     the same principal; a credential owned by anyone else revokes the just-issued cookie and fails
	///     closed. Returns JSON for <c>passkeys.js</c>.
	/// </summary>
	public async Task<IActionResult> OnPostPasskeyConfirmAsync(string? returnUrl = null)
	{
		if (!PasskeysEnabled) {
			return NotFound();
		}

		var user = await userManager.GetUserAsync(User);
		if (user is null) {
			return Challenge();
		}

		var limited = await ConsumePasskeyLimitAsync("passkey-stepup-assertion", user);
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
			return new JsonResult(new
			{
				error = GenericPasskeyFailureMessage,
			});
		}

		// Resolve the asserted credential only after success (§7.2) and require it to belong to the
		// signed-in principal: a step-up must never silently replace this session with another account's.
		if (!PasskeyAssertionCredential.TryReadCredentialId(credentialJson, out var credentialId)) {
			await signInManager.SignOutAsync();
			return new JsonResult(new
			{
				error = GenericPasskeyFailureMessage,
			});
		}

		var assertedUser = await userManager.FindByPasskeyIdAsync(credentialId);
		if (assertedUser is null || assertedUser.Id != user.Id) {
			// Options are scoped to the signed-in user, so the browser only ever offers this user's own
			// credentials -- a mismatch here means a hand-crafted assertion for another account, which
			// PasskeySignInAsync has just signed in. Sign that principal back out: a step-up must never
			// silently replace this session with another account's.
			await signInManager.SignOutAsync();
			return new JsonResult(new
			{
				error = GenericPasskeyFailureMessage,
			});
		}

		var target = returnUrl is not null && Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Page("/Index");
		return new JsonResult(new
		{
			redirect = target,
		});
	}

	private async Task LoadStateAsync(JobTrackIdentityUser user)
	{
		RequiresTwoFactorCode = await userManager.GetTwoFactorEnabledAsync(user);
		PasskeyStepUpAvailable = PasskeysEnabled && await UserHasPasskeyAsync(user);
	}

	private async Task<bool> UserHasPasskeyAsync(JobTrackIdentityUser user)
	{
		var passkeys = await jobTrackClient.Credentials.ListPasskeysAsync(
			new() {
				ActorUserId = user.AppUserId,
				IdentityUserId = user.Id,
			}, HttpContext.RequestAborted);
		return passkeys.Count > 0;
	}

	private async Task<IActionResult?> ConsumePasskeyLimitAsync(string purpose, JobTrackIdentityUser user)
	{
		var remoteAddress = GetRemoteAddress();
		var outcome = await loginAttemptRateLimiter.TryAcquireAsync(
			$"{purpose}:{remoteAddress}:{user.Id.ToString(CultureInfo.InvariantCulture)}", $"{purpose}:{remoteAddress}", HttpContext.RequestAborted);
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

	private IActionResult RedirectToReturnUrl(string? returnUrl) =>
		returnUrl is not null && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage("/Index");

	private static string GetPartitionKey(string remoteAddress, JobTrackIdentityUser user)
	{
		var normalizedUserName = user.NormalizedUserName
								 ?? user.UserName?.Trim().ToUpperInvariant()
								 ?? user.Id.ToString(CultureInfo.InvariantCulture);
		return $"confirm-access:{remoteAddress}:{normalizedUserName}";
	}

	private static string GetBackstopKey(string remoteAddress) => $"confirm-access:{remoteAddress}";

	private string GetRemoteAddress() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";

	public sealed class ConfirmAccessInput
	{
		[Required]
		[Display(Name = "Current password")]
		public string CurrentPassword { get; init; } = string.Empty;

		public string? TwoFactorCode { get; init; }
	}
}
