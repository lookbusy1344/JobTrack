namespace JobTrack.Web.Pages.Account;

using System.ComponentModel.DataAnnotations;
using Abstractions;
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

		try {
			var updated = await jobTrackClient.Credentials.ChangeOwnPasswordAsync(
				new() {
					ActorUserId = user.AppUserId,
					IdentityUserId = user.Id,
					CurrentPassword = Input.CurrentPassword,
					NewPassword = Input.NewPassword,
					CorrelationId = Guid.NewGuid(),
				});

			user.SecurityStamp = updated.SecurityStamp;
			user.ConcurrencyStamp = updated.ConcurrencyStamp;
			user.RequiresPasswordChange = false;
			await signInManager.RefreshSignInAsync(user);
		}
		catch (InvariantViolationException ex) when (ex.ConstraintId == "account-current-password-incorrect") {
			ErrorMessage = "The current password is incorrect.";
			return Page();
		}
		catch (InvariantViolationException ex) when (ex.ConstraintId == "account-new-password-policy") {
			ErrorMessage = ex.Message;
			return Page();
		}

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
