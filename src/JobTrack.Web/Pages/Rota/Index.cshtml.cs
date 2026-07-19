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
using Microsoft.AspNetCore.Mvc.Rendering;
using NodaTime;
using NodaTime.TimeZones;

/// <summary>
///     Personal schedule and exception management (plan §8.5 slice 6, spec §8.1/§8.3). The page uses
///     the coarse "administrator or worker" policy, then relies on
///     <see cref="Domain.Authorization.ScheduleAccessPolicy" /> inside the library for the finer
///     self-versus-other employee rule.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.ScheduleAdministration)]
public sealed class IndexModel(
	IJobTrackClient jobTrackClient,
	UserManager<JobTrackIdentityUser> userManager,
	IViewerTimeZoneResolver viewerTimeZoneResolver)
	: PageModel
{
	private const int MaxWeeklyIntervalSlots = 10;

	private IReadOnlyDictionary<AppUserId, EmployeeDirectoryEntry> _employeeDirectoryById =
		new Dictionary<AppUserId, EmployeeDirectoryEntry>();

	[BindProperty(SupportsGet = true)] public long? UserId { get; init; }

	[BindProperty] public AddVersionInput VersionInput { get; set; } = new();

	[BindProperty] public AddExceptionInput ExceptionInput { get; set; } = new();

	public long DisplayedUserId { get; private set; }

	public List<SelectListItem> UserOptions { get; private set; } = [];

	/// <summary>
	///     The displayed employee's display name and username, falling back to a numeric-id
	///     label if somehow absent from the loaded directory (see <see cref="IJobQueries.GetAllEmployeesAsync" />).
	/// </summary>
	public string DisplayedUserName => EmployeeDirectoryDisplay.Describe(_employeeDirectoryById, DisplayedUserId, "Unknown");

	public ScheduleSnapshotResult? Snapshot { get; private set; }

	/// <summary>
	///     The viewing actor's own time zone, for formatting and parsing exception boundaries (<see cref="InstantDisplay" />/
	///     <see cref="BackdateInstant" />) -- distinct from a schedule version's own configured <see cref="AddVersionInput.IanaTimeZone" />.
	/// </summary>
	public DateTimeZone ViewerZone { get; private set; } = DateTimeZoneProviders.Tzdb["Etc/UTC"];

	[TempData] public string? ErrorMessage { get; set; }

	[TempData] public string? SuccessMessage { get; set; }

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		for (var i = VersionInput.WeeklyIntervals.Count; i < MaxWeeklyIntervalSlots; i++) {
			VersionInput.WeeklyIntervals.Add(new());
		}

		await LoadAsync(actor.Value, UserId.HasValue ? new(UserId.Value) : actor.Value, cancellationToken);
		return Page();
	}

	public async Task<IActionResult> OnPostAddVersionAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		var targetUserId = UserId.HasValue ? new(UserId.Value) : actor.Value;

		// Every [BindProperty] on the page model is bound and validated regardless of which
		// handler ran, so ExceptionInput's [Required] fields would otherwise fail validation on
		// this handler even though they were never posted -- validate only VersionInput.
		ModelState.Clear();
		if (TryValidateModel(VersionInput, nameof(VersionInput))) {
			try {
				var zone = ScheduleZoneId.Resolve(VersionInput.IanaTimeZone);
				var weeklyIntervals = VersionInput.WeeklyIntervals
					.Where(slot => slot.Day is not null && slot.Start is not null && slot.End is not null)
					.Select(slot => new WeeklyInterval(
						slot.Day!.Value, new(slot.Start!.Value.Hour, slot.Start.Value.Minute),
						new(slot.End!.Value.Hour, slot.End.Value.Minute)))
					.ToArray();
				var effectiveStart = ToLocalDate(VersionInput.EffectiveStart);
				LocalDate? effectiveEnd = VersionInput.EffectiveEnd.HasValue ? ToLocalDate(VersionInput.EffectiveEnd.Value) : null;

				_ = await jobTrackClient.Schedules.AddScheduleVersionAsync(new() {
					Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
					UserId = targetUserId,
					Schedule = new(
						zone, effectiveStart, effectiveEnd, [.. weeklyIntervals]),
				}, cancellationToken);
				SuccessMessage = "Rota version added.";
			}
			catch (AuthorizationDeniedException) {
				return Forbid();
			}
			catch (EntityNotFoundException) {
				ErrorMessage = "That employee does not exist.";
			}
			catch (InvariantViolationException ex) {
				ErrorMessage = ex.Message;
			}
			catch (DateTimeZoneNotFoundException) {
				ErrorMessage = "That is not a recognized IANA time zone.";
			}

			return RedirectToPage(new { userId = UserId });
		}

		for (var i = VersionInput.WeeklyIntervals.Count; i < MaxWeeklyIntervalSlots; i++) {
			VersionInput.WeeklyIntervals.Add(new());
		}

		await LoadAsync(actor.Value, targetUserId, cancellationToken);
		return Page();
	}

	public async Task<IActionResult> OnPostAddExceptionAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		var targetUserId = UserId.HasValue ? new(UserId.Value) : actor.Value;
		var zone = await viewerTimeZoneResolver.ResolveAsync(actor.Value, cancellationToken);

		ModelState.Clear();
		if (TryValidateModel(ExceptionInput, nameof(ExceptionInput))) {
			if (!BackdateInstant.TryParse(ExceptionInput.Start, zone, out var start)
				|| !BackdateInstant.TryParse(ExceptionInput.End, zone, out var end)) {
				ModelState.AddModelError(string.Empty, "Start and end must each be a valid date and time.");
				for (var i = VersionInput.WeeklyIntervals.Count; i < MaxWeeklyIntervalSlots; i++) {
					VersionInput.WeeklyIntervals.Add(new());
				}

				await LoadAsync(actor.Value, targetUserId, cancellationToken);
				return Page();
			}

			try {
				var entry = new ScheduleExceptionEntry(
					ExceptionInput.Effect,
					new(start, end),
					ExceptionInput.RateOverride.HasValue ? new HourlyRate(ExceptionInput.RateOverride.Value) : null);

				_ = await jobTrackClient.Schedules.AddScheduleExceptionAsync(new() {
					Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
					UserId = targetUserId,
					Entry = entry,
					Reason = ExceptionInput.Reason,
				}, cancellationToken);
				SuccessMessage = "Rota exception added.";
			}
			catch (AuthorizationDeniedException) {
				return Forbid();
			}
			catch (EntityNotFoundException) {
				ErrorMessage = "That employee does not exist.";
			}
			catch (InvariantViolationException ex) {
				ErrorMessage = ex.Message;
			}
			catch (ArgumentException ex) {
				ErrorMessage = ex.Message;
			}

			return RedirectToPage(new { userId = UserId });
		}

		for (var i = VersionInput.WeeklyIntervals.Count; i < MaxWeeklyIntervalSlots; i++) {
			VersionInput.WeeklyIntervals.Add(new());
		}

		await LoadAsync(actor.Value, targetUserId, cancellationToken);
		return Page();
	}

	private async Task LoadAsync(AppUserId actor, AppUserId targetUserId, CancellationToken cancellationToken)
	{
		DisplayedUserId = targetUserId.Value;
		ViewerZone = await viewerTimeZoneResolver.ResolveAsync(actor, cancellationToken);

		var directory = await jobTrackClient.Query.GetAllEmployeesAsync(
			new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() } },
			cancellationToken);
		_employeeDirectoryById = directory.ToDictionary(entry => entry.Id);
		UserOptions = EmployeeDirectoryDisplay.BuildOptions(directory, new SelectListItem("Myself", string.Empty));

		try {
			Snapshot = await jobTrackClient.Query.GetScheduleAsync(
				new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() }, UserId = targetUserId }, cancellationToken);
		}
		catch (AuthorizationDeniedException) {
			ErrorMessage = "You may not view that employee's schedule.";
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

	private static LocalDate ToLocalDate(DateOnly date) => new(date.Year, date.Month, date.Day);

	public sealed class AddVersionInput
	{
		public AddVersionInput() => EffectiveStart = DateOnly.FromDateTime(DateTime.Today);

		[Required]
		[Display(Name = "Effective start")]
		public DateOnly EffectiveStart { get; set; }

		[Display(Name = "Effective end")] public DateOnly? EffectiveEnd { get; set; }

		[Required]
		[Display(Name = "IANA time zone")]
		public string IanaTimeZone { get; set; } = "Etc/UTC";

		public List<WeeklyIntervalSlotInput> WeeklyIntervals { get; set; } = [];
	}

	public sealed class WeeklyIntervalSlotInput
	{
		[Display(Name = "Day")] public IsoDayOfWeek? Day { get; set; }

		[Display(Name = "Start")] public TimeOnly? Start { get; set; }

		[Display(Name = "End")] public TimeOnly? End { get; set; }
	}

	public sealed class AddExceptionInput
	{
		[Required][Display(Name = "Effect")] public ScheduleExceptionEffect Effect { get; set; }

		[Required][Display(Name = "Start")] public string Start { get; set; } = string.Empty;

		[Required][Display(Name = "End")] public string End { get; set; } = string.Empty;

		[Display(Name = "Rate override")] public decimal? RateOverride { get; set; }

		[Required][Display(Name = "Reason")] public string Reason { get; set; } = string.Empty;
	}
}
