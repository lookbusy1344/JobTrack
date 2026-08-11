namespace JobTrack.Web.Pages.Jobs;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>
///     Compatibility redirect (unified-leaf-workflow plan Stage 5, ADR 0045): this route used to host
///     its own raw achievement-transition form. <c>/Jobs/Work</c> is now the single interactive
///     status+Sessions page, so a GET here (bookmarked, linked from an old page) lands on that page's
///     status section instead of rendering a competing form. The route itself is preserved for
///     compatibility; nothing here mutates state.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.JobWorkflow)]
public sealed class AchievementModel : PageModel
{
	[BindProperty(SupportsGet = true)] public long JobNodeId { get; init; }

	public IActionResult OnGet() => RedirectToPage("/Jobs/Work", null, new { leafNodeId = JobNodeId }, "status");
}
