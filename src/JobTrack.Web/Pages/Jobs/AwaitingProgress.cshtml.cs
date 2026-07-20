namespace JobTrack.Web.Pages.Jobs;

using System.Globalization;
using Abstractions;
using Application;
using Domain.Hierarchy;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using NodaTime;

/// <summary>
///     The flat "jobs awaiting progress" dashboard: leaves only, in priority/deadline order, for one
///     employee or everyone, optionally scoped to a subtree (linked from <c>Browse</c>'s toolbar), with
///     a one-click "Start work" per row (<see cref="IWorkCommands.StartWorkAsync" />) since this page is
///     precisely where a leaf needing exactly that action is surfaced. Viewing job data carries no
///     ownership-based authorization gate (spec §7.3), so the page uses the broad "any employee" policy,
///     matching <c>Browse</c>; the start-work handler carries no additional page-level policy either —
///     the command itself re-evaluates authorization per call.
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.AnyEmployee)]
public sealed class AwaitingProgressModel(
	IJobTrackClient jobTrackClient,
	UserManager<JobTrackIdentityUser> userManager,
	IViewerTimeZoneResolver viewerTimeZoneResolver,
	IClock clock) : PageModel
{
	// Fresh-eyes review §2.8: this dashboard is not paginated by an external API contract, so a
	// dashboard-appropriate fixed page size is enough -- no caller-supplied override.
	public const int PageSize = AwaitingProgressPaging.DefaultPageSize;

	private IReadOnlyDictionary<AppUserId, EmployeeDirectoryEntry> _employeeDirectoryById =
		new Dictionary<AppUserId, EmployeeDirectoryEntry>();

	/// <summary>Captured once per request, per ADR 0016's "one captured instant per operation".</summary>
	public Instant Now { get; } = clock.GetCurrentInstant();

	[BindProperty(SupportsGet = true)] public long? OwnerUserId { get; init; }

	[BindProperty(SupportsGet = true)] public int Offset { get; init; }

	/// <summary>
	///     When set, overrides <see cref="OwnerUserId" /> to show only the unassigned pool
	///     (ownership model §2.1) -- surfaces ready but unclaimed work.
	/// </summary>
	[BindProperty(SupportsGet = true)]
	public bool UnassignedOnly { get; init; }

	[BindProperty(SupportsGet = true)] public long? SubtreeRootId { get; init; }

	[TempData] public string? ErrorMessage { get; set; }

	[TempData] public string? SuccessMessage { get; set; }

	public JobNodeDetailResult? SubtreeRoot { get; private set; }

	public EquatableArray<AwaitingProgressEntry> Entries { get; private set; } = [];

	/// <summary>Whether another page of entries exists past <see cref="Entries" /> (fresh-eyes review §2.8's bounded-result contract).</summary>
	public bool HasMore { get; private set; }

	public IReadOnlyDictionary<JobNodeId, WorkSessionResult> ActiveSessionByLeaf { get; private set; } =
		new Dictionary<JobNodeId, WorkSessionResult>();

	/// <summary>
	///     The signed-in actor, so a row can tell its own active session apart from one
	///     <see cref="ActiveSessionByLeaf" /> surfaced because the actor may manage any leaf's session
	///     (Administrator/JobManager, ADR 0032) rather than because it is theirs.
	/// </summary>
	public AppUserId? CurrentActorId { get; private set; }

	/// <summary>The signed-in actor's own time zone, for formatting every timestamp on this page (<see cref="InstantDisplay" />).</summary>
	public DateTimeZone ViewerZone { get; private set; } = DateTimeZoneProviders.Tzdb["Etc/UTC"];

	public List<SelectListItem> OwnerOptions { get; private set; } = [];

	/// <summary>
	///     The page's own view state, replayed as hidden fields by every per-row work form so a start
	///     or finish lands back on the same owner, pool, and subtree filters rather than resetting the
	///     dashboard to everyone's work.
	/// </summary>
	public IReadOnlyDictionary<string, string?> RowStateFields => new Dictionary<string, string?> {
		["OwnerUserId"] = OwnerUserId?.ToString(CultureInfo.InvariantCulture),
		["UnassignedOnly"] = UnassignedOnly.ToString(),
		["SubtreeRootId"] = SubtreeRootId?.ToString(CultureInfo.InvariantCulture),
		["Offset"] = Offset.ToString(CultureInfo.InvariantCulture),
	};

	/// <summary>
	///     <paramref name="activeSession" />'s worker, formatted for display, but only when it is not
	///     <see cref="CurrentActorId" /> -- an admin managing another worker's session should see whose
	///     it is; the actor's own needs no such label.
	/// </summary>
	public string? ActiveSessionWorkedByOther(WorkSessionResult? activeSession) =>
		activeSession is not null && activeSession.WorkedByUserId != CurrentActorId
			? DescribeOwner(activeSession.WorkedByUserId)
			: null;

	/// <summary>
	///     Formats an owner id for display: display name and username when it resolves in
	///     the loaded workflow-employee directory, otherwise a fallback that still names the numeric
	///     id (covers an owner disabled or role-revoked since assignment — see
	///     <see cref="IJobQueries.GetEmployeeDirectoryAsync" />).
	/// </summary>
	public string DescribeOwner(AppUserId? ownerUserId) =>
		EmployeeDirectoryDisplay.Describe(_employeeDirectoryById, ownerUserId?.Value);

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadAsync(actor.Value, cancellationToken);
		return Page();
	}

	public async Task<IActionResult> OnPostStartWorkAsync(long jobNodeId, string? startedAt, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		try {
			var zone = await viewerTimeZoneResolver.ResolveAsync(actor.Value, cancellationToken);
			if (!BackdateInstant.TryParseOptional(startedAt, zone, out var startedAtInstant)) {
				ErrorMessage = "Enter a valid date and time.";
				return RedirectToPage(CurrentRouteValues());
			}

			_ = await jobTrackClient.Work.StartWorkAsync(
				new() {
					Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
					JobNodeId = new(jobNodeId),
					WorkedByUserId = actor.Value,
					StartedAt = startedAtInstant,
				}, cancellationToken);
			SuccessMessage = "Work started.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node does not exist.";
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = WorkSessionFailureDisplay.Describe(ex);
		}
		catch (PrerequisiteBlockedException) {
			ErrorMessage = "This leaf's prerequisites are not satisfied.";
		}

		return RedirectToPage(CurrentRouteValues());
	}

	public async Task<IActionResult> OnPostFinishAsync(
		long sessionId, long version, string? finishedAt, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		try {
			var zone = await viewerTimeZoneResolver.ResolveAsync(actor.Value, cancellationToken);
			if (!BackdateInstant.TryParseOptional(finishedAt, zone, out var finishedAtInstant)) {
				ErrorMessage = "Enter a valid date and time.";
				return RedirectToPage(CurrentRouteValues());
			}

			_ = await jobTrackClient.Work.FinishSessionAsync(new() {
				Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
				SessionId = new(sessionId),
				Version = version,
				FinishedAt = finishedAtInstant,
			}, cancellationToken);
			SuccessMessage = "Session finished.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That session does not exist.";
		}
		catch (ConcurrencyConflictException) {
			ErrorMessage = "Someone else changed this session since the page was loaded. The list below is refreshed.";
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = WorkSessionFailureDisplay.Describe(ex);
		}

		return RedirectToPage(CurrentRouteValues());
	}

	/// <summary>
	///     The page's own browsing context (mirrors <see cref="RowStateFields" />, minus the string
	///     conversion), replayed on the redirect every mutating handler ends with so the reloaded GET
	///     lands back on the same owner, pool, and subtree filters.
	/// </summary>
	private RouteValueDictionary CurrentRouteValues() => new() {
		["ownerUserId"] = OwnerUserId,
		["unassignedOnly"] = UnassignedOnly,
		["subtreeRootId"] = SubtreeRootId,
		["offset"] = Offset,
	};

	private async Task LoadAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		CurrentActorId = actor;
		ViewerZone = await viewerTimeZoneResolver.ResolveAsync(actor, cancellationToken);
		var context = new CommandContext { Actor = actor, CorrelationId = Guid.NewGuid() };

		var directory = await jobTrackClient.Query.GetEmployeeDirectoryAsync(
			new() { Context = context }, cancellationToken);
		_employeeDirectoryById = directory.ToDictionary(entry => entry.Id);
		OwnerOptions = EmployeeDirectoryDisplay.BuildOptions(directory, new SelectListItem("Everyone", string.Empty));

		try {
			if (SubtreeRootId.HasValue) {
				SubtreeRoot = await jobTrackClient.Query.GetJobNodeAsync(
					new() { Context = context, NodeId = new JobNodeId(SubtreeRootId.Value) }, cancellationToken);
			}

			var ownership = (UnassignedOnly, OwnerUserId) switch {
				(true, _) => OwnershipFilter.Unassigned,
				(false, { } ownerUserId) => OwnershipFilter.OwnedBy(new(ownerUserId)),
				(false, null) => OwnershipFilter.All,
			};

			var page = await jobTrackClient.Query.GetAwaitingProgressAsync(
				new() {
					Context = context,
					Ownership = ownership,
					SubtreeRootId = SubtreeRootId.HasValue ? new JobNodeId(SubtreeRootId.Value) : null,
					Offset = Math.Max(0, Offset),
					Limit = PageSize + 1,
				},
				cancellationToken);

			HasMore = page.Count > PageSize;
			Entries = HasMore ? [.. page.Take(PageSize)] : page;

			await LoadActiveSessionsAsync(context, cancellationToken);
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node does not exist.";
		}
	}

	private async Task LoadActiveSessionsAsync(CommandContext context, CancellationToken cancellationToken)
	{
		var leafIds = Entries.Select(entry => entry.Id).ToArray();
		if (leafIds.Length == 0) {
			ActiveSessionByLeaf = new Dictionary<JobNodeId, WorkSessionResult>();
			return;
		}

		try {
			var sessions = await jobTrackClient.Query.GetActiveSessionsAsync(new() { Context = context, LeafWorkIds = [.. leafIds] },
				cancellationToken);

			ActiveSessionByLeaf = WorkRowActiveSessions.ByLeaf(sessions);
		}
		catch (AuthorizationDeniedException) {
			ActiveSessionByLeaf = new Dictionary<JobNodeId, WorkSessionResult>();
		}
	}

	private async Task<AppUserId?> ResolveActorAsync()
	{
		var actor = await userManager.GetUserAsync(User);
		return actor?.AppUserId;
	}
}
