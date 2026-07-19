namespace JobTrack.Web.Pages.Account;

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
///     Self-service personal access token (PAT) management (security review remediation §2.2):
///     issue, list, and revoke the signed-in user's own tokens. Cookie-authenticated and
///     antiforgery-protected like every other Razor Page form — this is deliberately the only place a
///     PAT can be minted; the bearer API itself never issues one (ADR 0029). Administrators revoke
///     another user's tokens from <see cref="Admin.ManageEmployeeAccountModel" /> instead of here, since
///     <see cref="Domain.Authorization.PersonalAccessTokenAccessPolicy.CanIssue" /> never lets an actor
///     issue for anyone but themselves.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.AnyAuthenticatedUser)]
public sealed class PersonalAccessTokensModel(
	IJobTrackClient jobTrackClient,
	UserManager<JobTrackIdentityUser> userManager,
	IViewerTimeZoneResolver viewerTimeZoneResolver)
	: PageModel
{
	[BindProperty] public IssueTokenInput Issue { get; set; } = new();

	public EquatableArray<PersonalAccessTokenSummaryResult> Tokens { get; private set; } = [];

	/// <summary>The signed-in actor's own time zone, for formatting every token's timestamps (<see cref="InstantDisplay" />).</summary>
	public DateTimeZone ViewerZone { get; private set; } = DateTimeZoneProviders.Tzdb["Etc/UTC"];

	[TempData] public string? ErrorMessage { get; set; }

	[TempData] public string? SuccessMessage { get; set; }

	/// <summary>
	///     The newly issued plaintext token, rendered exactly once directly in this response. Never
	///     written to <c>TempData</c>, a cookie, or a log -- a page refresh or navigation away loses it
	///     permanently, matching the "shown once" contract (remediation §2.2/§2.4).
	/// </summary>
	public string? IssuedPlaintextToken { get; private set; }

	public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadTokensAsync(cancellationToken);

	/// <summary>
	///     Deliberately not converted to Post/Redirect/Get like every other mutating handler in this
	///     codebase: <see cref="IssuedPlaintextToken" /> must render in this exact response and would be
	///     lost across a redirect (see its own doc comment), so this one handler still risks a
	///     resubmission warning on refresh in exchange for never persisting the secret past this request.
	/// </summary>
	public async Task<IActionResult> OnPostIssueAsync(CancellationToken cancellationToken)
	{
		ModelState.Clear();
		if (!TryValidateModel(Issue, nameof(Issue))) {
			await LoadTokensAsync(cancellationToken);
			return Page();
		}

		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		try {
			var result = await jobTrackClient.Tokens.IssueAsync(new() {
				Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
				TargetUserId = actor.Value,
				Label = Issue.Label,
				ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(Issue.LifetimeDays),
			}, cancellationToken);

			IssuedPlaintextToken = result.Token;
			SuccessMessage = $"Token \"{result.Label}\" created. Copy it now — it will not be shown again.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = ex.Message;
		}

		Issue = new();
		await LoadTokensAsync(cancellationToken);
		return Page();
	}

	public async Task<IActionResult> OnPostRevokeAsync(long tokenId, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		try {
			await jobTrackClient.Tokens.RevokeAsync(
				new() {
					Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
					TargetUserId = actor.Value,
					TokenId = new(tokenId),
				}, cancellationToken);

			SuccessMessage = "Token revoked.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That token does not exist.";
		}

		return RedirectToPage();
	}

	private async Task LoadTokensAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return;
		}

		ViewerZone = await viewerTimeZoneResolver.ResolveAsync(actor.Value, cancellationToken);
		Tokens = await jobTrackClient.Tokens.ListAsync(
			new() { Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() }, TargetUserId = actor.Value }, cancellationToken);
	}

	private async Task<AppUserId?> ResolveActorAsync()
	{
		var actor = await userManager.GetUserAsync(User);
		return actor?.AppUserId;
	}

	public sealed class IssueTokenInput
	{
		/// <summary>
		///     UI-only default/bound; the authoritative cap is <see cref="Domain.Authorization.PersonalAccessTokenPolicy.MaxLifetime" />,
		///     enforced server-side regardless of what this form submits.
		/// </summary>
		private const int DefaultLifetimeDays = 30;

		private const int MaxLifetimeDaysForValidationAttribute = 365;

		[Required][MaxLength(200)] public string Label { get; init; } = string.Empty;

		[Range(1, MaxLifetimeDaysForValidationAttribute)]
		public int LifetimeDays { get; init; } = DefaultLifetimeDays;
	}
}
