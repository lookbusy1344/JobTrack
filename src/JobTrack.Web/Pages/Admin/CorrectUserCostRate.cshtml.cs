namespace JobTrack.Web.Pages.Admin;

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
///     Corrects a historical user cost rate's effective range and amount in place (ADR 0003), mirroring
///     <see cref="Jobs.CorrectSessionModel" />'s single-step form shape.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.RateAdministration)]
public sealed class CorrectUserCostRateModel(IJobTrackClient jobTrackClient, UserManager<JobTrackIdentityUser> userManager) : PageModel
{
	[BindProperty(SupportsGet = true)] public long UserId { get; init; }

	[BindProperty(SupportsGet = true)] public long RateId { get; init; }

	[BindProperty] public CorrectInput Input { get; set; } = new();

	public UserCostRateResult? Rate { get; private set; }

	public string? ErrorMessage { get; private set; }

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadRateAsync(actor.Value, cancellationToken);
		if (Rate is { } rate) {
			Input.AmountPerHour = rate.Rate.Rate.AmountPerHour;
			Input.EffectiveStart = rate.Rate.EffectiveStart.ToDateTimeOffset();
			Input.EffectiveEnd = rate.Rate.EffectiveEnd?.ToDateTimeOffset();
		}

		return Page();
	}

	public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadRateAsync(actor.Value, cancellationToken);
		if (Rate is null || !ModelState.IsValid) {
			return Page();
		}

		try {
			_ = await jobTrackClient.Rates.CorrectUserCostRateAsync(new() {
				Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
				RateId = new(RateId),
				UserId = new AppUserId(UserId),
				Version = Rate.Version,
				Reason = Input.Reason,
				Rate = new(
					new(Input.AmountPerHour),
					Instant.FromDateTimeOffset(Input.EffectiveStart),
					Input.EffectiveEnd is { } end ? Instant.FromDateTimeOffset(end) : null),
			}, cancellationToken);

			return RedirectToPage("/Admin/Rates", new { userId = UserId });
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That cost rate no longer exists.";
		}
		catch (ConcurrencyConflictException) {
			ErrorMessage = "Someone else changed this cost rate since the form was loaded. Review and try again.";
			await LoadRateAsync(actor.Value, cancellationToken);
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = ex.Message;
		}
		catch (ArgumentOutOfRangeException ex) {
			ErrorMessage = ex.Message;
		}

		return Page();
	}

	private async Task LoadRateAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		try {
			var snapshot = await jobTrackClient.Query.GetRatesAsync(
				new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() }, UserId = new(UserId) }, cancellationToken);

			Rate = snapshot.UserCostRates.FirstOrDefault(r => r.Id == new UserCostRateId(RateId));
			if (Rate is null) {
				ErrorMessage = "That cost rate does not exist.";
			}
		}
		catch (AuthorizationDeniedException) {
			ErrorMessage = "You may not view or correct that employee's rates.";
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
		[Required] public DateTimeOffset EffectiveStart { get; set; }

		public DateTimeOffset? EffectiveEnd { get; set; }

		[Required] public decimal AmountPerHour { get; set; }

		[Required] public string Reason { get; set; } = string.Empty;
	}
}
