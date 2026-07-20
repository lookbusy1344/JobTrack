namespace JobTrack.Web.Pages.Jobs;

using Abstractions;
using Application;
using Domain.Hierarchy;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NodaTime;

/// <summary>
///     Cost report with rate provenance and current prerequisite diagnostics (plan §8.5 slice 8, spec
///     "detailed cost results with allocated intervals, applicable rates, rate provenance, and current
///     prerequisite-state diagnostics"). Gated by <see cref="JobTrackPolicyNames.RateRead" />, which
///     mirrors <see cref="Domain.Authorization.CostAccessPolicy" /> exactly (Administrator or
///     CostViewer) — unlike readiness, cost visibility is never an unqualified baseline capability.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.RateRead)]
public sealed class CostReportModel(
	IJobTrackClient jobTrackClient,
	UserManager<JobTrackIdentityUser> userManager,
	IViewerTimeZoneResolver viewerTimeZoneResolver,
	IClock clock) : PageModel
{
	[BindProperty(SupportsGet = true)] public long NodeId { get; init; }

	/// <summary>The instant to evaluate cost as of, as a <c>datetime-local</c> string; blank defaults to now (spec §10: costs are dynamic).</summary>
	[BindProperty(SupportsGet = true)]
	public string? AsOf { get; init; }

	public string? ErrorMessage { get; private set; }

	public JobNodeDetailResult? Node { get; private set; }

	public CostDetailsResult? CostDetails { get; private set; }

	public ReadinessResult? Readiness { get; private set; }

	/// <summary>The signed-in actor's own time zone, for formatting every timestamp on this page (<see cref="InstantDisplay" />).</summary>
	public DateTimeZone ViewerZone { get; private set; } = DateTimeZoneProviders.Tzdb["Etc/UTC"];

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await userManager.GetUserAsync(User);
		if (actor is null) {
			return Challenge();
		}

		ViewerZone = await viewerTimeZoneResolver.ResolveAsync(actor.AppUserId, cancellationToken);
		if (!BackdateInstant.TryParseOptional(AsOf, ViewerZone, out var asOfInstant)) {
			ErrorMessage = "Enter a valid date and time.";
			return Page();
		}

		var context = new CommandContext { Actor = actor.AppUserId, CorrelationId = Guid.NewGuid() };
		var nodeId = new JobNodeId(NodeId);
		var asOf = asOfInstant ?? clock.GetCurrentInstant();

		try {
			Node = await jobTrackClient.Query.GetJobNodeAsync(new() { Context = context, NodeId = nodeId }, cancellationToken);
			Readiness = await jobTrackClient.Query.GetReadinessAsync(new() { Context = context, NodeId = nodeId }, cancellationToken);
			CostDetails = await jobTrackClient.Costs.GetCostDetailsAsync(new() { Context = context, NodeId = nodeId, AsOf = asOf },
				cancellationToken);
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node does not exist.";
		}
		catch (MissingRateException) {
			ErrorMessage = "No rate resolves for one or more contributing sessions, so cost cannot be calculated.";
		}

		return Page();
	}
}
