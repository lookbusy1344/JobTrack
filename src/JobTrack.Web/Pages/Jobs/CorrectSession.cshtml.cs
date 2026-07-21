namespace JobTrack.Web.Pages.Jobs;

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
///     Corrects a historical session's start and/or finish instants (plan §8.5 slice 4, spec §4.4):
///     "Workers may correct their own historical sessions... Job managers and administrators may
///     correct any session." Every correction requires a reason and produces an audit record of the
///     previous and replacement values; unlike the structural workflows in slice 3, spec §4.4 requires
///     no second-person approval, so this is a single-step form rather than preview-then-confirm.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.JobWorkflow)]
public sealed class CorrectSessionModel(
	IJobTrackClient jobTrackClient,
	UserManager<JobTrackIdentityUser> userManager,
	IViewerTimeZoneResolver viewerTimeZoneResolver)
	: PageModel
{
	[BindProperty(SupportsGet = true)] public long LeafNodeId { get; init; }

	[BindProperty(SupportsGet = true)] public long WorkedByUserId { get; init; }

	[BindProperty(SupportsGet = true)] public long SessionId { get; init; }

	[BindProperty] public CorrectInput Input { get; set; } = new();

	public WorkSessionResult? Session { get; private set; }

	public string? ErrorMessage { get; private set; }

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadSessionAsync(actor.Value, cancellationToken);
		var result = Session;
		if (result is not null) {
			var zone = await viewerTimeZoneResolver.ResolveAsync(actor.Value, cancellationToken);
			Input.StartedAt = BackdateInstant.ToDateTimeLocalValue(result.StartedAt, zone);
			Input.FinishedAt = result.FinishedAt.HasValue ? BackdateInstant.ToDateTimeLocalValue(result.FinishedAt.Value, zone) : null;
		}

		return Page();
	}

	public Task<IActionResult> OnPostAsync(CancellationToken cancellationToken) =>
		SaveAsync(false, cancellationToken);

	/// <summary>
	///     Clears the finished time and saves in one step — reopening the session to active — still
	///     requiring a reason like any other correction. The one-click affordance exists because
	///     blanking a browser's <c>datetime-local</c> control by hand is fiddly and inconsistent
	///     across browsers; this never bypasses the reason requirement (ModelState is still validated).
	/// </summary>
	public Task<IActionResult> OnPostClearFinishAsync(CancellationToken cancellationToken) =>
		SaveAsync(true, cancellationToken);

	private async Task<IActionResult> SaveAsync(bool clearFinish, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadSessionAsync(actor.Value, cancellationToken);
		if (Session is null || !ModelState.IsValid) {
			return Page();
		}

		var zone = await viewerTimeZoneResolver.ResolveAsync(actor.Value, cancellationToken);
		if (!BackdateInstant.TryParse(Input.StartedAt, zone, out var startedAtInstant)) {
			ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.StartedAt)}", "Enter a valid date and time.");
			return Page();
		}

		Instant? finishedAtInstant = null;
		if (clearFinish) {
			Input.FinishedAt = null;
		} else if (!BackdateInstant.TryParseOptional(Input.FinishedAt, zone, out finishedAtInstant)) {
			ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.FinishedAt)}", "Enter a valid date and time.");
			return Page();
		}

		var request = new CorrectSessionRequest {
			Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
			SessionId = new(SessionId),
			StartedAt = startedAtInstant,
			FinishedAt = finishedAtInstant,
			Reason = Input.Reason,
			Version = Session.Version,
		};

		try {
			_ = await jobTrackClient.Work.CorrectSessionAsync(request, cancellationToken);
			// No workedByUserId: returning to Work restores the viewer's remembered filter (or its
			// permission-aware default), rather than snapping the Sessions view to the corrected worker.
			return RedirectToPage("/Jobs/Work", new { leafNodeId = LeafNodeId });
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That session no longer exists.";
		}
		catch (ConcurrencyConflictException) {
			ErrorMessage = "Someone else changed this session since the form was loaded. The latest values are shown below — review and try again.";
			await LoadSessionAsync(actor.Value, cancellationToken);
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = ex.Message;
		}

		return Page();
	}

	private async Task LoadSessionAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		try {
			var sessions = await jobTrackClient.Query.GetLeafSessionsAsync(
				new() {
					Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() },
					LeafWorkId = new(LeafNodeId),
					WorkedByUserId = new AppUserId(WorkedByUserId),
				}, cancellationToken);

			Session = sessions.FirstOrDefault(s => s.Id == new WorkSessionId(SessionId));
			if (Session is null) {
				ErrorMessage = "That session does not exist.";
			}
		}
		catch (AuthorizationDeniedException) {
			ErrorMessage = "You may not view or correct that worker's sessions.";
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node does not exist.";
		}
	}

	private async Task<AppUserId?> ResolveActorAsync()
	{
		var actor = await userManager.GetUserAsync(User);
		return actor?.AppUserId;
	}

	public sealed class CorrectInput
	{
		[Required] public string StartedAt { get; set; } = string.Empty;

		public string? FinishedAt { get; set; }

		[Required] public string Reason { get; set; } = string.Empty;
	}
}
