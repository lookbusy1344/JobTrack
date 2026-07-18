namespace JobTrack.Web.Pages.Rota;

using System.ComponentModel.DataAnnotations;
using Abstractions;
using Application;
using Domain.Schedules;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NodaTime;

/// <summary>
///     Corrects a historical schedule exception's effect, interval, and rate override in place (ADR
///     0003), mirroring <see cref="Jobs.CorrectSessionModel" />'s single-step form shape.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.ScheduleAdministration)]
public sealed class CorrectExceptionModel(IJobTrackClient jobTrackClient, UserManager<JobTrackIdentityUser> userManager) : PageModel
{
	[BindProperty(SupportsGet = true)] public long UserId { get; init; }

	[BindProperty(SupportsGet = true)] public long ExceptionId { get; init; }

	[BindProperty] public CorrectInput Input { get; set; } = new();

	public ScheduleExceptionResult? Exception { get; private set; }

	public string? ErrorMessage { get; private set; }

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadExceptionAsync(actor.Value, cancellationToken);
		if (Exception is { } exception) {
			Input.Effect = exception.Entry.Effect;
			Input.Start = exception.Entry.Interval.Start.ToDateTimeOffset();
			Input.End = exception.Entry.Interval.End.ToDateTimeOffset();
			Input.RateOverride = exception.Entry.RateOverride?.AmountPerHour;
			Input.Reason = exception.Reason;
		}

		return Page();
	}

	public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadExceptionAsync(actor.Value, cancellationToken);
		if (Exception is null || !ModelState.IsValid) {
			return Page();
		}

		try {
			var entry = new ScheduleExceptionEntry(
				Input.Effect,
				new(Instant.FromDateTimeOffset(Input.Start), Instant.FromDateTimeOffset(Input.End)),
				Input.RateOverride is { } rate ? new HourlyRate(rate) : null);

			_ = await jobTrackClient.Schedules.CorrectScheduleExceptionAsync(new() {
				Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
				ExceptionId = new(ExceptionId),
				UserId = new AppUserId(UserId),
				Version = Exception.Version,
				Reason = Input.Reason,
				Entry = entry,
			}, cancellationToken);

			return RedirectToPage("/Rota/Index", new { userId = UserId });
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That schedule exception no longer exists.";
		}
		catch (ConcurrencyConflictException) {
			ErrorMessage = "Someone else changed this schedule exception since the form was loaded. Review and try again.";
			await LoadExceptionAsync(actor.Value, cancellationToken);
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = ex.Message;
		}
		catch (ArgumentException ex) {
			ErrorMessage = ex.Message;
		}

		return Page();
	}

	private async Task LoadExceptionAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		try {
			var snapshot = await jobTrackClient.Query.GetScheduleAsync(
				new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() }, UserId = new(UserId) }, cancellationToken);

			Exception = snapshot.Exceptions.FirstOrDefault(e => e.Id == new ScheduleExceptionId(ExceptionId));
			if (Exception is null) {
				ErrorMessage = "That schedule exception does not exist.";
			}
		}
		catch (AuthorizationDeniedException) {
			ErrorMessage = "You may not view or correct that employee's schedule.";
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That employee does not exist.";
		}
	}

	private async Task<AppUserId?> ResolveActorAsync()
	{
		var actor = await userManager.GetUserAsync(User);
		return actor?.AppUserId;
	}

	public sealed class CorrectInput
	{
		[Required] public ScheduleExceptionEffect Effect { get; set; }

		[Required] public DateTimeOffset Start { get; set; }

		[Required] public DateTimeOffset End { get; set; }

		public decimal? RateOverride { get; set; }

		[Required] public string Reason { get; set; } = string.Empty;
	}
}
