namespace JobTrack.Web.Pages.Account;

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

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
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.AnyAuthenticatedUser)]
public sealed class ConfirmAccessModel(
	SignInManager<JobTrackIdentityUser> signInManager,
	UserManager<JobTrackIdentityUser> userManager,
	LoginAttemptRateLimiter loginAttemptRateLimiter) : PageModel
{
	private const string RateLimitedMessage = "Too many authentication attempts. Retry after the current window elapses.";

	[BindProperty] public ConfirmAccessInput Input { get; set; } = new();

	public bool RequiresTwoFactorCode { get; private set; }

	public string? ErrorMessage { get; private set; }

	public string? ReturnUrl { get; private set; }

	public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
	{
		var user = await userManager.GetUserAsync(User);
		if (user is null) {
			return Challenge();
		}

		ReturnUrl = returnUrl;
		RequiresTwoFactorCode = await userManager.GetTwoFactorEnabledAsync(user);
		return Page();
	}

	public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
	{
		var user = await userManager.GetUserAsync(User);
		if (user is null) {
			return Challenge();
		}

		ReturnUrl = returnUrl;
		RequiresTwoFactorCode = await userManager.GetTwoFactorEnabledAsync(user);
		var remoteAddress = GetRemoteAddress();
		if (!loginAttemptRateLimiter.TryAcquire(GetPartitionKey(remoteAddress, user), GetBackstopKey(remoteAddress))) {
			Response.StatusCode = StatusCodes.Status429TooManyRequests;
			ErrorMessage = RateLimitedMessage;
			return Page();
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

		return returnUrl is not null && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage("/Index");
	}

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
