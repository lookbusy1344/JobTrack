namespace JobTrack.Web.Pages.Account;

using System.ComponentModel.DataAnnotations;
using Application;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>
///     §8.5 slice 1: forced password change. Reachable by any authenticated user (not just one with
///     <see cref="JobTrackIdentityUser.RequiresPasswordChange" /> set — any signed-in account may
///     change its password voluntarily), but <see cref="RequiresPasswordChangePageFilter" /> redirects
///     here unconditionally while the flag is set.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.AnyAuthenticatedUser)]
public sealed class ChangePasswordModel(
	SignInManager<JobTrackIdentityUser> signInManager,
	UserManager<JobTrackIdentityUser> userManager,
	IJobTrackClient jobTrackClient) : PageModel
{
	[BindProperty] public ChangePasswordInput Input { get; set; } = new();

	public string? ErrorMessage { get; private set; }

	public void OnGet()
	{
	}

	public async Task<IActionResult> OnPostAsync()
	{
		if (!ModelState.IsValid) {
			return Page();
		}

		var user = await userManager.GetUserAsync(User);
		if (user is null) {
			return Challenge();
		}

		var changeResult = await userManager.ChangePasswordAsync(user, Input.CurrentPassword, Input.NewPassword);
		if (!changeResult.Succeeded) {
			ErrorMessage = string.Join(" ", changeResult.Errors.Select(error => error.Description));
			return Page();
		}

		user.RequiresPasswordChange = false;
		_ = await userManager.UpdateAsync(user);
		await signInManager.RefreshSignInAsync(user);

		// A self-service password change is the same credential-sensitivity class as an
		// administrator-driven reset -- it must revoke every live personal access token too
		// (ADR 0029), not only the web session RefreshSignInAsync already rotates.
		await jobTrackClient.Tokens.RevokeAllAsync(
			new() { Context = new() { Actor = user.AppUserId, CorrelationId = Guid.NewGuid() }, TargetUserId = user.AppUserId });

		await AuthenticationAudit.RecordKnownAsync(jobTrackClient, user, AuthenticationAuditEventKind.PasswordChanged);

		return RedirectToPage("/Index");
	}

	public sealed class ChangePasswordInput
	{
		[Required] public string CurrentPassword { get; init; } = string.Empty;

		[Required] public string NewPassword { get; init; } = string.Empty;

		[Required]
		[Compare(nameof(NewPassword))]
		public string ConfirmNewPassword { get; init; } = string.Empty;
	}
}
