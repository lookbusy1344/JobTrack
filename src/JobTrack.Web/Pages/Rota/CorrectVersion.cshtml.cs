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
using NodaTime.TimeZones;

/// <summary>
///     Corrects a historical schedule version's effective range, zone, and weekly intervals in place
///     (ADR 0003), mirroring <see cref="Jobs.CorrectSessionModel" />'s single-step form shape: a
///     mandatory reason, no second-person approval, and the freshly loaded row's own version drives
///     optimistic concurrency rather than a posted hidden field.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.ScheduleAdministration)]
public sealed class CorrectVersionModel(IJobTrackClient jobTrackClient, UserManager<JobTrackIdentityUser> userManager) : PageModel
{
	private const int MaxWeeklyIntervalSlots = 10;

	[BindProperty(SupportsGet = true)] public long UserId { get; init; }

	[BindProperty(SupportsGet = true)] public long VersionId { get; init; }

	[BindProperty] public CorrectInput Input { get; set; } = new();

	public ScheduleVersionResult? Version { get; private set; }

	public string? ErrorMessage { get; private set; }

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadVersionAsync(actor.Value, cancellationToken);
		var result = Version;
		if (result is not null) {
			Input.IanaTimeZone = result.Schedule.Zone.Id;
			Input.EffectiveStart = ToDateOnly(result.Schedule.EffectiveStart);
			Input.EffectiveEnd = result.Schedule.EffectiveEnd.HasValue ? ToDateOnly(result.Schedule.EffectiveEnd.Value) : null;
			Input.WeeklyIntervals = [
				.. result.Schedule.WeeklyIntervals.Select(interval =>
					new IndexModel.WeeklyIntervalSlotInput {
						Day = interval.Day, Start = ToTimeOnly(interval.Start), End = ToTimeOnly(interval.End),
					}),
			];
		}

		PadWeeklyIntervals();
		return Page();
	}

	public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadVersionAsync(actor.Value, cancellationToken);
		if (Version is null || !ModelState.IsValid) {
			PadWeeklyIntervals();
			return Page();
		}

		try {
			var zone = ScheduleZoneId.Resolve(Input.IanaTimeZone);
			var weeklyIntervals = Input.WeeklyIntervals
				.Where(slot => slot.Day is not null && slot.Start is not null && slot.End is not null)
				.Select(slot => new WeeklyInterval(
					slot.Day!.Value, new(slot.Start!.Value.Hour, slot.Start.Value.Minute),
					new(slot.End!.Value.Hour, slot.End.Value.Minute)))
				.ToArray();

			_ = await jobTrackClient.Schedules.CorrectScheduleVersionAsync(new() {
				Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
				VersionId = new(VersionId),
				UserId = new AppUserId(UserId),
				Version = Version.Version,
				Reason = Input.Reason,
				Schedule = new(
					zone, ToLocalDate(Input.EffectiveStart),
					Input.EffectiveEnd.HasValue ? ToLocalDate(Input.EffectiveEnd.Value) : null, [.. weeklyIntervals]),
			}, cancellationToken);

			return RedirectToPage("/Rota/Index", new { userId = UserId });
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That schedule version no longer exists.";
		}
		catch (ConcurrencyConflictException) {
			ErrorMessage = "Someone else changed this schedule version since the form was loaded. Review and try again.";
			await LoadVersionAsync(actor.Value, cancellationToken);
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = ex.Message;
		}
		catch (DateTimeZoneNotFoundException) {
			ErrorMessage = "That is not a recognized IANA time zone.";
		}

		PadWeeklyIntervals();
		return Page();
	}

	private void PadWeeklyIntervals()
	{
		for (var i = Input.WeeklyIntervals.Count; i < MaxWeeklyIntervalSlots; i++) {
			Input.WeeklyIntervals.Add(new());
		}
	}

	private async Task LoadVersionAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		try {
			var snapshot = await jobTrackClient.Query.GetScheduleAsync(
				new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() }, UserId = new(UserId) }, cancellationToken);

			Version = snapshot.Versions.FirstOrDefault(v => v.Id == new ScheduleVersionId(VersionId));
			if (Version is null) {
				ErrorMessage = "That schedule version does not exist.";
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

	private static DateOnly ToDateOnly(LocalDate date) => new(date.Year, date.Month, date.Day);

	private static TimeOnly ToTimeOnly(LocalTime time) => new(time.Hour, time.Minute);

	private static LocalDate ToLocalDate(DateOnly date) => new(date.Year, date.Month, date.Day);

	public sealed class CorrectInput
	{
		[Required] public string IanaTimeZone { get; set; } = "Etc/UTC";

		[Required] public DateOnly EffectiveStart { get; set; }

		public DateOnly? EffectiveEnd { get; set; }

		public List<IndexModel.WeeklyIntervalSlotInput> WeeklyIntervals { get; set; } = [];

		[Required] public string Reason { get; set; } = string.Empty;
	}
}
