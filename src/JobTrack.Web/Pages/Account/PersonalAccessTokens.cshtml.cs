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
	IViewerTimeZoneResolver viewerTimeZoneResolver,
	PendingPatDeliveryStore pendingDeliveryStore)
	: PageModel
{
	[BindProperty] public IssueTokenInput Issue { get; set; } = new();

	public EquatableArray<PersonalAccessTokenSummaryResult> Tokens { get; private set; } = [];

	/// <summary>The signed-in actor's own time zone, for formatting every token's timestamps (<see cref="InstantDisplay" />).</summary>
	public DateTimeZone ViewerZone { get; private set; } = DateTimeZoneProviders.Tzdb["Etc/UTC"];

	[TempData] public string? ErrorMessage { get; set; }

	[TempData] public string? SuccessMessage { get; set; }

	/// <summary>
	///     The newly issued plaintext token, rendered exactly once directly in this GET response after
	///     <see cref="PendingPatDeliveryStore.TryConsume" /> hands it over. Never written to
	///     <c>TempData</c>, a cookie, a URL, or a log -- a page refresh or navigation away loses it
	///     permanently, matching the "shown once" contract (remediation §2.2/§2.4/§2.7).
	/// </summary>
	public string? IssuedPlaintextToken { get; private set; }

	/// <summary>
	///     <paramref name="issued" />, when present, is the opaque handle from the redirect at the end
	///     of <see cref="OnPostIssueAsync" />. It atomically consumes the one-use delivery slot (scoped
	///     to the signed-in actor) so the plaintext renders exactly once, on this GET, never on the POST
	///     response itself -- refreshing this page after a successful issuance re-runs a harmless read.
	/// </summary>
	public async Task OnGetAsync(string? issued, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is not null && issued is not null && Guid.TryParse(issued, out var handle)) {
			if (pendingDeliveryStore.TryConsume(handle, actor.Value, out var label, out var plaintext)) {
				IssuedPlaintextToken = plaintext;
				SuccessMessage = $"Token \"{label}\" created. Copy it now — it will not be shown again.";
			} else {
				ErrorMessage = "That token's secret is no longer available to display. If you did not copy it, revoke it and issue a new one.";
			}
		}

		await LoadTokensAsync(cancellationToken);
	}

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

		if (!pendingDeliveryStore.TryReserve(actor.Value, out var handle)) {
			ErrorMessage = "Too many pending token deliveries right now. Wait a moment and try again.";
			await LoadTokensAsync(cancellationToken);
			return Page();
		}

		try {
			var result = await jobTrackClient.Tokens.IssueAsync(new() {
				Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
				TargetUserId = actor.Value,
				Label = Issue.Label,
				Lifetime = Duration.FromDays(Issue.LifetimeDays),
			}, cancellationToken);

			pendingDeliveryStore.Publish(handle, result.Label, result.Token);
			return RedirectToPage(null, new { issued = handle });
		}
		catch (AuthorizationDeniedException) {
			pendingDeliveryStore.Release(handle);
			return Forbid();
		}
		catch (InvariantViolationException ex) {
			pendingDeliveryStore.Release(handle);
			ErrorMessage = ex.Message;
		}
		catch {
			pendingDeliveryStore.Release(handle);
			throw;
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
