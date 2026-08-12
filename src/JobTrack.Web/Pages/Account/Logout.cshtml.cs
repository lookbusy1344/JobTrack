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
	public void OnGet() { }

	public async Task<IActionResult> OnPostAsync()
	{
		var user = await userManager.GetUserAsync(User);
		await signInManager.SignOutAsync();
		// §2.5 of the 2026-07-28 fresh-eyes review: clears remembered filter memory (and any future
		// principal-bound session state) so it cannot survive into whichever employee signs in next on
		// this browser.
		PrincipalBoundSessionState.Reset(HttpContext);
		if (user is not null) {
			await AuthenticationAudit.RecordKnownAsync(jobTrackClient, user, AuthenticationAuditEventKind.Logout);
		}

		return RedirectToPage("/Index");
	}
}
