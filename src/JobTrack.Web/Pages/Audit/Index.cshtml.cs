namespace JobTrack.Web.Pages.Audit;

using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NodaTime;

/// <summary>
///     Audit browsing with permission-sensitive detail (plan §8.5 slice 9, spec §16). Gated by
///     <see
///         cref="JobTrackPolicyNames.AuditSearch" />
///     , which mirrors
///     <see
///         cref="Domain.Authorization.AuditAccessPolicy" />
///     exactly (Administrator or Auditor) — the audit
///     log itself is never an unqualified baseline capability, unlike ordinary job/schedule visibility.
///     A rate/cost-bearing event's before/after payload is separately redacted per event by
///     <see
///         cref="IAuditQueries" />
///     for a caller who lacks cost-viewing permission, even an Auditor.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.AuditSearch)]
public sealed class IndexModel(
	IJobTrackClient jobTrackClient,
	UserManager<JobTrackIdentityUser> userManager,
	IViewerTimeZoneResolver viewerTimeZoneResolver)
	: PageModel
{
	[BindProperty(SupportsGet = true)] public long? ActorId { get; init; }

	[BindProperty(SupportsGet = true)] public string? EntityType { get; init; }

	[BindProperty(SupportsGet = true)] public long? EntityId { get; init; }

	[BindProperty(SupportsGet = true)] public Guid? CorrelationId { get; init; }

	[BindProperty(SupportsGet = true)] public string? From { get; init; }

	[BindProperty(SupportsGet = true)] public string? To { get; init; }

	public string? ErrorMessage { get; private set; }

	public IReadOnlyList<AuditEventResult> Events { get; private set; } = [];

	/// <summary>
	///     The signed-in actor's own time zone, for formatting every event's <c>OccurredAt</c> and parsing the <see cref="From" />/<see cref="To" />
	///     filter (<see cref="InstantDisplay" />).
	/// </summary>
	public DateTimeZone ViewerZone { get; private set; } = DateTimeZoneProviders.Tzdb["Etc/UTC"];

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await userManager.GetUserAsync(User);
		if (actor is null) {
			return Challenge();
		}

		ViewerZone = await viewerTimeZoneResolver.ResolveAsync(actor.AppUserId, cancellationToken);
		var context = new CommandContext { Actor = actor.AppUserId, CorrelationId = Guid.NewGuid() };

		try {
			Events = await jobTrackClient.Audit.SearchAuditEventsAsync(new() {
				Context = context,
				Filter = new() {
					ActorId = ActorId.HasValue ? new AppUserId(ActorId.Value) : null,
					EntityType = EntityType,
					EntityId = EntityId,
					CorrelationId = CorrelationId,
					From = BackdateInstant.TryParse(From, ViewerZone, out var from) ? from : null,
					To = BackdateInstant.TryParse(To, ViewerZone, out var to) ? to : null,
				},
			}, cancellationToken);
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}

		return Page();
	}
}
