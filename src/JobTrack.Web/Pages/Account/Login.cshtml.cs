namespace JobTrack.Web.Pages.Account;

using System.ComponentModel.DataAnnotations;
using Application;
using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>
///     §8.5 slice 1: sign-in. The failure message is identical for an unknown username, a wrong
///     password, and a locked-out account — spec §7.1: "generic login failure messages shall not
///     reveal whether an employee account exists" (threat model row 2).
/// </summary>
public sealed class LoginModel(
	SignInManager<JobTrackIdentityUser> signInManager,
	UserManager<JobTrackIdentityUser> userManager,
	LoginAttemptRateLimiter loginAttemptRateLimiter,
	IJobTrackClient jobTrackClient) : PageModel
{
	private const string GenericFailureMessage = "The username or password is incorrect.";
	private const string RateLimitedMessage = "Too many sign-in attempts. Retry after the current window elapses.";

	[BindProperty] public LoginInput Input { get; set; } = new();

	public string? ErrorMessage { get; private set; }

	public void OnGet()
	{
	}

	public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
	{
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

		return returnUrl is not null && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage("/Index");
	}

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
