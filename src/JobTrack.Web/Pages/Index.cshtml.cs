namespace JobTrack.Web.Pages;

using Application;
using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>
///     The landing page carries no content of its own — it routes straight into the app: signed-out
///     visitors go to the login page. Signed-in employees go to their configured home node (see
///     <see cref="EmployeeProfileResult.HomeNodeId" />), or -- when none is set -- to the tree root. Both
///     are shown unfiltered: the landing applies no ownership filter, so every employee sees the whole
///     active tree by default and narrows it themselves via the Browse filter. This landing-only
///     redirect is the sole place a home node applies; any request that already names a specific
///     <see cref="Jobs.BrowseModel.NodeId" /> (a deep link, <c>returnUrl</c>, manual navigation) bypasses
///     it entirely.
/// </summary>
public sealed class IndexModel(IJobTrackClient jobTrackClient, UserManager<JobTrackIdentityUser> userManager) : PageModel
{
	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		if (User.Identity?.IsAuthenticated != true) {
			return RedirectToPage("/Account/Login");
		}

		var actor = await userManager.GetUserAsync(User);
		if (actor is null) {
			return RedirectToPage("/Account/Login");
		}

		var profile = await jobTrackClient.Query.GetEmployeeProfileAsync(
			new() { Context = new() { Actor = actor.AppUserId, CorrelationId = Guid.NewGuid() }, TargetUserId = actor.AppUserId },
			cancellationToken);

		return profile.HomeNodeId is { } homeNodeId
			? RedirectToPage("/Jobs/Browse", new { NodeId = homeNodeId.Value, ArchiveFilter = JobArchiveFilter.ActiveOnly })
			: RedirectToPage("/Jobs/Browse", new { ArchiveFilter = JobArchiveFilter.ActiveOnly });
	}
}
