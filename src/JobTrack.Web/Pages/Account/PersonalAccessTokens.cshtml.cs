namespace JobTrack.Web.Pages.Account;

using System.ComponentModel.DataAnnotations;
using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
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
	IDataProtectionProvider dataProtectionProvider)
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
	///     <see cref="PendingPatDeliveryCookie.TryConsume" /> hands it over. Never written to
	///     <c>TempData</c>, a cookie value the browser retains beyond the delivery window, a URL, or a
	///     log -- a page refresh or navigation away loses it permanently, matching the "shown once"
	///     contract (remediation §2.2/§2.4/§2.7).
	/// </summary>
	public string? IssuedPlaintextToken { get; private set; }

	/// <summary>
	///     <paramref name="issued" /> is set on the redirect at the end of <see cref="OnPostIssueAsync" />
	///     to trigger consuming the delivery cookie -- the plaintext itself never rides in the URL.
	///     <see cref="PendingPatDeliveryCookie.TryConsume" /> decrypts and deletes it in one step
	///     (scoped to the signed-in actor) so it renders exactly once, on this GET, never on the POST
	///     response itself -- refreshing this page after a successful issuance re-runs a harmless read
	///     that now finds nothing pending.
	/// </summary>
	public async Task OnGetAsync(bool issued, CancellationToken cancellationToken)
	{
		var actor = await userManager.GetAppUserIdAsync(User);
		if (actor is not null && issued) {
			if (PendingPatDeliveryCookie.TryConsume(HttpContext, dataProtectionProvider, actor.Value, out var label, out var plaintext)) {
				IssuedPlaintextToken = plaintext;
				SuccessMessage = $"Token \"{label}\" created. Copy it now — it will not be shown again.";
			} else {
				ErrorMessage = "That token's secret is no longer available to display. If you did not copy it, revoke it and issue a new one.";
			}
		}

		await LoadTokensAsync(cancellationToken);
	}

	/// <summary>ADR 0057 (§2.2): PAT issuance is the finding's own worked example of a sensitive operation.</summary>
	[RequiresRecentAuthentication]
	public async Task<IActionResult> OnPostIssueAsync(CancellationToken cancellationToken)
	{
		ModelState.Clear();
		if (!TryValidateModel(Issue, nameof(Issue))) {
			await LoadTokensAsync(cancellationToken);
			return Page();
		}

		var actor = await userManager.GetAppUserIdAsync(User);
		if (actor is null) {
			return Challenge();
		}

		// Reserve-before-command (ADR 0066 Stage 4): refuse before minting a token this label could
		// never be delivered for, rather than after. There is no cross-request capacity to exhaust
		// once delivery is client-side, so this is a pure size check, not a shared-pool reservation.
		if (!PendingPatDeliveryCookie.CanDeliver(Issue.Label)) {
			ErrorMessage = "That label is too long to deliver. Use a shorter one and try again.";
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

			// Publish only after the database commit above -- an unpublished failure between commit
			// and here simply has no delivery cookie, matching the old store's accepted window.
			PendingPatDeliveryCookie.Publish(HttpContext, dataProtectionProvider, actor.Value, result.Label, result.Token);
			return RedirectToPage(null, new { issued = true });
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
		var actor = await userManager.GetAppUserIdAsync(User);
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
		var actor = await userManager.GetAppUserIdAsync(User);
		if (actor is null) {
			return;
		}

		ViewerZone = await viewerTimeZoneResolver.ResolveAsync(actor.Value, cancellationToken);
		Tokens = await jobTrackClient.Tokens.ListAsync(
			new() { Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() }, TargetUserId = actor.Value }, cancellationToken);
	}

	public sealed class IssueTokenInput
	{
		/// <summary>
		///     UI-only default/bound; the authoritative cap is <see cref="Domain.Authorization.PersonalAccessTokenPolicy.MaxLifetime" />,
		///     enforced server-side regardless of what this form submits.
		/// </summary>
		private const int DefaultLifetimeDays = 30;

		private const int MaxLifetimeDaysForValidationAttribute = 365;

		[Required][MaxCodePointLength(200)] public string Label { get; init; } = string.Empty;

		[Range(1, MaxLifetimeDaysForValidationAttribute)]
		[Display(Name = "Lifetime (days)")]
		public int LifetimeDays { get; init; } = DefaultLifetimeDays;
	}
}
