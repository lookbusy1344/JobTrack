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
///     Corrects a historical node rate override's node, effective range, and amount in place (ADR
///     0003), mirroring <see cref="Jobs.CorrectSessionModel" />'s single-step form shape.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.RateAdministration)]
public sealed class CorrectNodeRateOverrideModel(IJobTrackClient jobTrackClient, UserManager<JobTrackIdentityUser> userManager) : PageModel
{
	[BindProperty(SupportsGet = true)] public long UserId { get; init; }

	[BindProperty(SupportsGet = true)] public long OverrideId { get; init; }

	[BindProperty] public CorrectInput Input { get; set; } = new();

	public NodeRateOverrideResult? Override { get; private set; }

	public string? ErrorMessage { get; private set; }

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadOverrideAsync(actor.Value, cancellationToken);
		if (Override is { } over) {
			Input.NodeId = over.Override.NodeId.Value;
			Input.AmountPerHour = over.Override.Rate.AmountPerHour;
			Input.EffectiveStart = over.Override.EffectiveStart.ToDateTimeOffset();
			Input.EffectiveEnd = over.Override.EffectiveEnd?.ToDateTimeOffset();
		}

		return Page();
	}

	public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadOverrideAsync(actor.Value, cancellationToken);
		if (Override is null || !ModelState.IsValid) {
			return Page();
		}

		try {
			_ = await jobTrackClient.Rates.CorrectNodeRateOverrideAsync(new() {
				Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
				OverrideId = new(OverrideId),
				UserId = new AppUserId(UserId),
				Version = Override.Version,
				Reason = Input.Reason,
				Override = new(
					new(Input.NodeId),
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
			ErrorMessage = "That override or job node no longer exists.";
		}
		catch (ConcurrencyConflictException) {
			ErrorMessage = "Someone else changed this override since the form was loaded. Review and try again.";
			await LoadOverrideAsync(actor.Value, cancellationToken);
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = ex.Message;
		}
		catch (ArgumentOutOfRangeException ex) {
			ErrorMessage = ex.Message;
		}

		return Page();
	}

	private async Task LoadOverrideAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		try {
			var snapshot = await jobTrackClient.Query.GetRatesAsync(
				new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() }, UserId = new(UserId) }, cancellationToken);

			Override = snapshot.NodeRateOverrides.FirstOrDefault(o => o.Id == new NodeRateOverrideId(OverrideId));
			if (Override is null) {
				ErrorMessage = "That override does not exist.";
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
		[Required] public long NodeId { get; set; }

		[Required] public DateTimeOffset EffectiveStart { get; set; }

		public DateTimeOffset? EffectiveEnd { get; set; }

		[Required] public decimal AmountPerHour { get; set; }

		[Required] public string Reason { get; set; } = string.Empty;
	}
}
