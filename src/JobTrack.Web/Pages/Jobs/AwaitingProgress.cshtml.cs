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
///     a one-click "Start session" per row (<see cref="IWorkCommands.StartWorkAsync" />) since this page is
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

	// Browse-sessions filter memory: the owner selector's last "person or Everyone" choice is
	// remembered per session under this key so returning to the dashboard restores it.
	private const string OwnerFilterSessionKey = "Jobs.AwaitingProgress.Owner";

	private IReadOnlyDictionary<AppUserId, EmployeeDirectoryEntry> _employeeDirectoryById =
		new Dictionary<AppUserId, EmployeeDirectoryEntry>();

	/// <summary>Captured once per request, per ADR 0016's "one captured instant per operation".</summary>
	public Instant Now { get; } = clock.GetCurrentInstant();

	// Settable so LoadAsync can replace an omitted value with the remembered choice (or the default),
	// which the owner <select> (asp-for) and every replayed filter/route value then reflect.
	[BindProperty(SupportsGet = true)] public long? OwnerUserId { get; set; }

	[BindProperty(SupportsGet = true)] public int Offset { get; init; }

	/// <summary>
	///     When set, overrides <see cref="OwnerUserId" /> to show only the unassigned pool
	///     (ownership model §2.1) -- surfaces ready but unclaimed work.
	/// </summary>
	[BindProperty(SupportsGet = true)]
	public bool UnassignedOnly { get; init; }

	// Settable so LoadAsync can replace an omitted value with the actor's home node (see LoadAsync),
	// which every replayed filter/route value then reflects.
	[BindProperty(SupportsGet = true)] public long? SubtreeRootId { get; set; }

	/// <summary>
	///     When set, keeps the dashboard unscoped even though <see cref="SubtreeRootId" /> is absent —
	///     distinguishes "no scope specified yet" (defaults to the actor's home node, if any) from "the
	///     whole tree was explicitly chosen" (the "Show the whole tree" link below). Without this flag,
	///     re-submitting the filter form after choosing the whole tree would re-default straight back to
	///     the home node, since <see cref="SubtreeRootId" /> is null either way.
	/// </summary>
	[BindProperty(SupportsGet = true)]
	public bool ShowWholeTree { get; init; }

	/// <summary>
	///     When non-blank, restricts to leaves whose description contains this text (case insensitive) —
	///     scopes the same owner/subtree-filtered candidate set as the rest of this dashboard's filters,
	///     unlike Browse's Search flow which queries the whole tree.
	/// </summary>
	[BindProperty(SupportsGet = true)]
	public string? SearchText { get; init; }

	[TempData] public string? ErrorMessage { get; set; }

	[TempData] public string? SuccessMessage { get; set; }

	public JobNodeDetailResult? SubtreeRoot { get; private set; }

	public EquatableArray<AwaitingProgressEntry> Entries { get; private set; } = [];

	/// <summary>Whether another page of entries exists past <see cref="Entries" /> (fresh-eyes review §2.8's bounded-result contract).</summary>
	public bool HasMore { get; private set; }

	/// <summary>
	///     Every active session on each rendered leaf, never collapsed to one representative
	///     (<see cref="ActiveSessionGrouping.Group" />). <see cref="WorkRowActionsModel" /> derives the
	///     viewer's own session, every other worker's, and the count from this per row.
	/// </summary>
	public IReadOnlyDictionary<JobNodeId, EquatableArray<WorkSessionResult>> ActiveSessionsByLeaf { get; private set; } =
		new Dictionary<JobNodeId, EquatableArray<WorkSessionResult>>();

	/// <summary>
	///     Whether the actor may manage sessions on each rendered leaf (<see cref="IJobQueries.GetSessionManageCapabilitiesAsync" />,
	///     ADR 0044 Stage 4/6) — a batched rendering hint for another worker's exact finish and the
	///     "Start for…" disclosure; the command itself remains the authoritative gate.
	/// </summary>
	public IReadOnlyDictionary<JobNodeId, bool> CanManageByLeaf { get; private set; } = new Dictionary<JobNodeId, bool>();

	/// <summary>
	///     The signed-in actor, so a row can tell its own active session apart from one
	///     <see cref="ActiveSessionsByLeaf" /> surfaced because the actor may manage any leaf's session
	///     (Administrator/JobManager, ADR 0032) rather than because it is theirs.
	/// </summary>
	public AppUserId? CurrentActorId { get; private set; }

	/// <summary>Every enabled workflow employee, for the "Start for…" worker picker (plan §2.5).</summary>
	public IReadOnlyList<SelectListItem> StartForWorkerOptions { get; private set; } = [];

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
		["ShowWholeTree"] = ShowWholeTree.ToString(),
		["SearchText"] = SearchText,
		["Offset"] = Offset.ToString(CultureInfo.InvariantCulture),
	};

	/// <summary>
	///     Builds a <see cref="WorkRowActionsModel" /> for <paramref name="leafId" />, sourcing its active-session collection and manage capability
	///     from the batched loads above.
	/// </summary>
	public WorkRowActionsModel WorkRowActionsFor(JobNodeId leafId, Achievement? achievement) => new() {
		LeafNodeId = leafId.Value,
		ViewerId = CurrentActorId ?? new AppUserId(0),
		ActiveSessions = ActiveSessionsByLeaf.GetValueOrDefault(leafId, []),
		CanManage = CanManageByLeaf.GetValueOrDefault(leafId, false),
		Achievement = achievement,
		IsArchived = false,
		ViewerZone = ViewerZone,
		StartHandler = "StartWork",
		StartNodeFieldName = "jobNodeId",
		PageStateFields = RowStateFields,
		StartForWorkerOptions = StartForWorkerOptions,
	};

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
			SuccessMessage = "Session started.";
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

	/// <summary>
	///     Starts a session for <paramref name="startForUserId" /> rather than the signed-in actor
	///     (plan §2.5 "Starting for another worker") — mirrors <c>Browse</c>'s <c>StartFor</c> handler.
	///     Authorization is not rechecked here beyond signing in; <c>StartWorkAsync</c> re-evaluates
	///     <see cref="Domain.Authorization.WorkSessionAccessPolicy.CanManage" /> for the acting user.
	/// </summary>
	public async Task<IActionResult> OnPostStartForAsync(
		long jobNodeId, long? startForUserId, string? startedAt, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		if (startForUserId is not long targetUserId) {
			ErrorMessage = "Choose a worker to start for.";
			return RedirectToPage(CurrentRouteValues());
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
					WorkedByUserId = new(targetUserId),
					StartedAt = startedAtInstant,
				}, cancellationToken);
			SuccessMessage = "Session started.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node or worker does not exist.";
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = WorkSessionFailureDisplay.Describe(ex);
		}
		catch (PrerequisiteBlockedException) {
			ErrorMessage = "This leaf's prerequisites are not satisfied.";
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
		["showWholeTree"] = ShowWholeTree,
		["searchText"] = SearchText,
		["offset"] = Offset,
	};

	private async Task LoadAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		CurrentActorId = actor;
		ViewerZone = await viewerTimeZoneResolver.ResolveAsync(actor, cancellationToken);
		var context = new CommandContext { Actor = actor, CorrelationId = Guid.NewGuid() };

		// Owner filter is remembered across visits; the whole tree is browsable, so the default when
		// nothing is remembered is "Everyone" (no permission-scoped fallback like Work's).
		OwnerUserId = FilterMemory.Resolve(
			HttpContext.Session, OwnerFilterSessionKey, Request.Query.ContainsKey(nameof(OwnerUserId)), OwnerUserId, null);

		var directory = await jobTrackClient.Query.GetEmployeeDirectoryAsync(
			new() { Context = context }, cancellationToken);
		_employeeDirectoryById = directory.ToDictionary(entry => entry.Id);
		OwnerOptions = EmployeeDirectoryDisplay.BuildOptions(directory, new SelectListItem("Everyone", string.Empty));
		StartForWorkerOptions = EmployeeDirectoryDisplay.BuildOptions(directory);

		// A bare visit (no subtree specified, whole tree not explicitly chosen -- e.g. the header nav
		// link) defaults to the actor's own home node rather than the entire tree; ShowWholeTree is the
		// escape hatch that survives round-trips through the filter form and PRG redirects, since
		// SubtreeRootId is null either way once that link is followed.
		if (!SubtreeRootId.HasValue && !ShowWholeTree) {
			var profile = await jobTrackClient.Query.GetEmployeeProfileAsync(
				new() { Context = context, TargetUserId = actor }, cancellationToken);
			SubtreeRootId = profile.HomeNodeId?.Value;
		}

		try {
			if (SubtreeRootId.HasValue) {
				SubtreeRoot = await jobTrackClient.Query.GetJobNodeAsync(
					new() { Context = context, NodeId = new JobNodeId(SubtreeRootId.Value) }, cancellationToken);
			}

			var ownership = (UnassignedOnly, OwnerUserId) switch {
				(true, _) => OwnershipFilter.Unassigned,
				(false, long ownerUserId) => OwnershipFilter.OwnedBy(new(ownerUserId)),
				(false, null) => OwnershipFilter.All,
			};

			var page = await jobTrackClient.Query.GetAwaitingProgressAsync(
				new() {
					Context = context,
					Ownership = ownership,
					SubtreeRootId = SubtreeRootId.HasValue ? new JobNodeId(SubtreeRootId.Value) : null,
					SearchText = SearchText,
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
			ActiveSessionsByLeaf = new Dictionary<JobNodeId, EquatableArray<WorkSessionResult>>();
			CanManageByLeaf = new Dictionary<JobNodeId, bool>();
			return;
		}

		try {
			var sessions = await jobTrackClient.Query.GetActiveSessionsAsync(new() { Context = context, LeafWorkIds = [.. leafIds] },
				cancellationToken);

			ActiveSessionsByLeaf = ActiveSessionGrouping.Group(sessions);
		}
		catch (AuthorizationDeniedException) {
			ActiveSessionsByLeaf = new Dictionary<JobNodeId, EquatableArray<WorkSessionResult>>();
		}

		var capabilities = await jobTrackClient.Query.GetSessionManageCapabilitiesAsync(
			new() { Context = context, LeafWorkIds = [.. leafIds] }, cancellationToken);
		CanManageByLeaf = capabilities.ToDictionary(c => c.LeafWorkId, c => c.CanManage);
	}

	private async Task<AppUserId?> ResolveActorAsync()
	{
		var actor = await userManager.GetUserAsync(User);
		return actor?.AppUserId;
	}
}
