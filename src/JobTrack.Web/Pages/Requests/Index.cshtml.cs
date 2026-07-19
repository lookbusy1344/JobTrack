namespace JobTrack.Web.Pages.Requests;

using System.ComponentModel.DataAnnotations;
using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NodaTime;

/// <summary>
///     Requester self-service: submit a new request into an eligible holding area, and see a flat list
///     of the requester's own submitted requests (ADR 0033, plan §8). A single combined page, the same
///     shape as <see cref="Account.PersonalAccessTokensModel" /> — the task-focused "first screen" the
///     plan describes, not a separate create page. Restricted to <see cref="EmployeeRole.Requester" />
///     via <see cref="JobTrackPolicyNames.RequesterAccess" />; the operational job tree, work sessions,
///     rates, costs, and audit history are never reachable from here.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.RequesterAccess)]
public sealed class IndexModel(
	IJobTrackClient jobTrackClient,
	UserManager<JobTrackIdentityUser> userManager,
	IViewerTimeZoneResolver viewerTimeZoneResolver)
	: PageModel
{
	[BindProperty] public SubmitRequestInput Submit { get; set; } = new();

	public EquatableArray<HoldingAreaSummaryResult> EligibleHoldingAreas { get; private set; } = [];

	public EquatableArray<JobRequestSummaryResult> MyRequests { get; private set; } = [];

	/// <summary>The signed-in actor's own time zone, for formatting <see cref="MyRequests" />'s <c>SubmittedAt</c> (<see cref="InstantDisplay" />).</summary>
	public DateTimeZone ViewerZone { get; private set; } = DateTimeZoneProviders.Tzdb["Etc/UTC"];

	[TempData] public string? ErrorMessage { get; set; }

	[TempData] public string? SuccessMessage { get; set; }

	public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

	public async Task<IActionResult> OnPostSubmitAsync(CancellationToken cancellationToken)
	{
		ModelState.Clear();
		if (!TryValidateModel(Submit, nameof(Submit))) {
			await LoadAsync(cancellationToken);
			return Page();
		}

		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		try {
			var result = await jobTrackClient.Requests.SubmitAsync(
				new() {
					Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
					HoldingAreaId = new(Submit.HoldingAreaId),
					Description = Submit.Description,
				}, cancellationToken);

			SuccessMessage = $"Request \"{JobNodeDisplay.Title(result.Description, result.JobNodeId.Value)}\" submitted.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That holding area no longer exists.";
		}

		return RedirectToPage();
	}

	private async Task LoadAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return;
		}

		ViewerZone = await viewerTimeZoneResolver.ResolveAsync(actor.Value, cancellationToken);
		var context = new CommandContext { Actor = actor.Value, CorrelationId = Guid.NewGuid() };
		EligibleHoldingAreas = await jobTrackClient.Requests.GetEligibleHoldingAreasAsync(context, cancellationToken);
		MyRequests = await jobTrackClient.Requests.GetMyRequestsAsync(context, cancellationToken);
	}

	private async Task<AppUserId?> ResolveActorAsync()
	{
		var actor = await userManager.GetUserAsync(User);
		return actor?.AppUserId;
	}

	public sealed class SubmitRequestInput
	{
		[Required][MaxLength(4000)] public string Description { get; init; } = string.Empty;

		[Range(1, long.MaxValue, ErrorMessage = "Choose a holding area.")]
		public long HoldingAreaId { get; init; }
	}
}
