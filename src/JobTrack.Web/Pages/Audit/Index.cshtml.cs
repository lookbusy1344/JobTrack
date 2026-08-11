namespace JobTrack.Web.Pages.Audit;

using System.ComponentModel.DataAnnotations;
using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
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
	private IReadOnlyDictionary<AppUserId, EmployeeDirectoryEntry> _employeeDirectoryById =
		new Dictionary<AppUserId, EmployeeDirectoryEntry>();

	[BindProperty(SupportsGet = true)]
	[Display(Name = "Actor")]
	public long? ActorId { get; init; }

	public List<SelectListItem> ActorOptions { get; private set; } = [];

	[BindProperty(SupportsGet = true)]
	[Display(Name = "Entity type")]
	public string? EntityType { get; init; }

	[BindProperty(SupportsGet = true)]
	[Display(Name = "Entity")]
	public long? EntityId { get; init; }

	[BindProperty(SupportsGet = true)]
	[Display(Name = "Correlation ID")]
	public Guid? CorrelationId { get; init; }

	[BindProperty(SupportsGet = true)] public string? From { get; init; }

	[BindProperty(SupportsGet = true)] public string? To { get; init; }

	[BindProperty(SupportsGet = true)] public string? Cursor { get; init; }

	public string? ErrorMessage { get; private set; }

	public IReadOnlyList<AuditEventResult> Events { get; private set; } = [];

	public string? NextCursor { get; private set; }

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

		var directory = await LoadEmployeeDirectoryAsync(actor.AppUserId, cancellationToken);
		_employeeDirectoryById = directory.ToDictionary(entry => entry.Id);
		ActorOptions = EmployeeDirectoryDisplay.BuildOptions(directory, new SelectListItem("All", string.Empty));

		if (!BackdateInstant.TryParseOptional(From, ViewerZone, out var from)
			|| !BackdateInstant.TryParseOptional(To, ViewerZone, out var to)) {
			ErrorMessage = "Enter a valid date and time for each audit time filter.";
			return Page();
		}

		try {
			var result = await jobTrackClient.Audit.SearchAuditEventsAsync(new() {
				Context = context,
				Filter = new() {
					ActorId = ActorId.HasValue ? new AppUserId(ActorId.Value) : null,
					EntityType = EntityType,
					EntityId = EntityId,
					CorrelationId = CorrelationId,
					From = from,
					To = to,
				},
				Cursor = Cursor,
			}, cancellationToken);

			Events = result.Events;
			NextCursor = result.ContinuationCursor;
		}
		catch (ArgumentException) {
			ErrorMessage = "The audit search page reference is invalid; start a new search.";
			return Page();
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}

		return Page();
	}

	/// <summary>
	///     "Display name (username)" for an event's actor, "system" for an actor-less event (e.g. an
	///     unknown-subject login failure), matching <see cref="EmployeeDirectoryDisplay" />'s rendering
	///     used elsewhere for the same <see cref="AppUserId" />.
	/// </summary>
	public string DescribeActor(AppUserId? actorId) => EmployeeDirectoryDisplay.Describe(_employeeDirectoryById, actorId?.Value, "system");

	/// <summary>
	///     <see cref="IJobQueries.GetAllEmployeesAsync" /> requires <see cref="EmployeeRole.Administrator" />;
	///     an <see cref="EmployeeRole.Auditor" /> without that role (a valid holder of
	///     <see cref="JobTrackPolicyNames.AuditSearch" />) falls back to
	///     <see cref="IJobQueries.GetEmployeeDirectoryAsync" />, which every operational role including
	///     Auditor may use (<see cref="Domain.Authorization.JobDataAccessPolicy.CanBrowseJobData" />).
	/// </summary>
	private async Task<EquatableArray<EmployeeDirectoryEntry>> LoadEmployeeDirectoryAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		try {
			return await jobTrackClient.Query.GetAllEmployeesAsync(
				new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() } }, cancellationToken);
		}
		catch (AuthorizationDeniedException) {
			return await jobTrackClient.Query.GetEmployeeDirectoryAsync(
				new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() } }, cancellationToken);
		}
	}
}
