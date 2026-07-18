namespace JobTrack.Web.Pages.Jobs;

using System.ComponentModel.DataAnnotations;
using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>
///     Achievement updates (plan §8.5 slice 5, ADR 0001): transitions a leaf's <c>LeafWork</c>
///     achievement state. Carries no page-level authorization policy —
///     <see cref="Domain.Authorization.AchievementAccessPolicy" /> is re-evaluated by
///     <see cref="IWorkCommands.SetAchievementAsync" /> itself, including the reopening-authority rule
///     (Administrator/JobManager only, regardless of ownership), so an unauthorized attempt is denied
///     by the command, not the page.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.JobWorkflow)]
public sealed class AchievementModel(IJobTrackClient jobTrackClient, UserManager<JobTrackIdentityUser> userManager) : PageModel
{
	[BindProperty(SupportsGet = true)] public long JobNodeId { get; init; }

	[BindProperty] public long OriginalVersion { get; set; }

	[BindProperty] public UpdateInput Input { get; set; } = new();

	public LeafWorkResult? LeafWork { get; private set; }

	public string? ErrorMessage { get; private set; }

	public string? SuccessMessage { get; private set; }

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadAsync(actor.Value, cancellationToken);
		if (LeafWork is { } leafWork) {
			Input.NewAchievement = leafWork.Achievement;
			OriginalVersion = leafWork.Version;
		}

		return Page();
	}

	public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		if (!ModelState.IsValid) {
			await LoadAsync(actor.Value, cancellationToken);
			return Page();
		}

		try {
			_ = await jobTrackClient.Work.SetAchievementAsync(new() {
				Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
				JobNodeId = new(JobNodeId),
				NewAchievement = Input.NewAchievement,
				Reason = Input.Reason,
				Version = OriginalVersion,
			}, cancellationToken);

			return RedirectToPage("/Jobs/Browse", new { nodeId = JobNodeId });
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "This leaf has no work attached.";
		}
		catch (ConcurrencyConflictException) {
			ErrorMessage =
				"Someone else changed this leaf's achievement since the form was loaded. The latest state is shown below — review and try again.";
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = ex.Message;
		}
		catch (PrerequisiteBlockedException) {
			ErrorMessage = "This leaf's prerequisites are not satisfied, so it cannot be marked complete.";
		}

		await LoadAsync(actor.Value, cancellationToken);
		return Page();
	}

	private async Task LoadAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		try {
			LeafWork = await jobTrackClient.Query.GetLeafWorkAsync(
				new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() }, JobNodeId = new(JobNodeId) }, cancellationToken);
			OriginalVersion = LeafWork.Version;
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "This leaf has no work attached.";
		}
	}

	private async Task<AppUserId?> ResolveActorAsync()
	{
		var actor = await userManager.GetUserAsync(User);
		return actor?.AppUserId;
	}

	public sealed class UpdateInput
	{
		[Required] public Achievement NewAchievement { get; set; }

		[Required] public string Reason { get; set; } = string.Empty;
	}
}
