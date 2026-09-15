namespace JobTrack.Web.Pages.Account;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>
///     Compatibility route (ADR 0071 §1.1): TOTP two-factor management moved onto <c>/Account/Security</c>,
///     the one account-security hub. This permanent redirect keeps old bookmarks and links working.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.AnyAuthenticatedUser)]
public sealed class ManageTwoFactorModel : PageModel
{
	public IActionResult OnGet() => RedirectToPagePermanent("/Account/Security");
}
