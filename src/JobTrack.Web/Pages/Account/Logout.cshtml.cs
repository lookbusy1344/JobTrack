namespace JobTrack.Web.Pages.Account;

using Application;
using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public sealed class LogoutModel(
	SignInManager<JobTrackIdentityUser> signInManager,
	UserManager<JobTrackIdentityUser> userManager,
	IJobTrackClient jobTrackClient) : PageModel
{
	public void OnGet()
	{
	}

	public async Task<IActionResult> OnPostAsync()
	{
		var user = await userManager.GetUserAsync(User);
		await signInManager.SignOutAsync();
		if (user is not null) {
			await AuthenticationAudit.RecordKnownAsync(jobTrackClient, user, AuthenticationAuditEventKind.Logout);
		}

		return RedirectToPage("/Index");
	}
}
