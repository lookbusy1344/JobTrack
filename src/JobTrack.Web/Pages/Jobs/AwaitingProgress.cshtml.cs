namespace JobTrack.Web.Pages.Jobs;

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Abstractions;
using Application;
using Domain.Hierarchy;
using Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
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
	IClock clock,
	IDataProtectionProvider dataProtectionProvider,
	ILogger<AwaitingProgressModel> logger) : PageModel
{
	// Fresh-eyes review §2.8: this dashboard is not paginated by an external API contract, so a
	// dashboard-appropriate fixed page size is enough -- no caller-supplied override.
	public const int PageSize = AwaitingProgressPaging.DefaultPageSize;

	// Filter memory: every filter this dashboard offers -- owner, the unassigned pool, search text,
	// blocked-job exclusion, and the in-progress narrowing -- is remembered per session under these
	// keys, so returning to the dashboard (e.g. via the header link, carrying no parameters at all)
	// restores the view the user last chose rather than resetting it. Two things are deliberately not
	// remembered: Offset, a position within a result rather than a filter, and the subtree scope pair
	// (ADR 0052, revised) -- a URL naming no node always scopes to the actor's home node, so the
	// header link is a reliable way home rather than a replay of wherever they last were.
	private const string OwnerFilterSessionKey = "Jobs.AwaitingProgress.Owner";
	private const string UnassignedOnlyFilterSessionKey = "Jobs.AwaitingProgress.UnassignedOnly";
	private const string SearchTextFilterSessionKey = "Jobs.AwaitingProgress.SearchText";
	private const string ExcludeBlockedFilterSessionKey = "Jobs.AwaitingProgress.ExcludeBlocked";
	private const string InProgressOnlyFilterSessionKey = "Jobs.AwaitingProgress.InProgressOnly";
	private const string ActiveWorkerFilterSessionKey = "Jobs.AwaitingProgress.ActiveWorker";

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
	///     (ownership model §2.1) -- surfaces ready but unclaimed work. Settable for the same
	///     remembered-choice reason as <see cref="OwnerUserId" />.
	/// </summary>
	[BindProperty(SupportsGet = true)]
	public bool UnassignedOnly { get; set; }

	/// <summary>
	///     When set, leaves blocked by an unsatisfied prerequisite are left out entirely rather than
	///     listed below the ready ones -- nothing can be done about them (ADR 0051: blocked is a state,
	///     not an error, but it is also not actionable work).
	/// </summary>
	[BindProperty(SupportsGet = true)]
	public bool ExcludeBlocked { get; set; }

	/// <summary>
	///     When set, restricts to leaves whose work has started and reached no closure
	///     (<see cref="Achievement.InProgress" />) — whether someone is clocked on right now or the leaf
	///     is paused. Composes with the owner selector above rather than replacing it, so "what is this
	///     person part-way through" is the two filters together.
	/// </summary>
	[BindProperty(SupportsGet = true)]
	public bool InProgressOnly { get; set; }

	/// <summary>
	///     When set, restricts to leaves this employee is working right now — those carrying an open
	///     session of theirs. A different question from <see cref="InProgressOnly" />, which asks what
	///     the achievement says and so keeps a paused leaf: with a person chosen, a paused leaf has no
	///     open session and drops out either way. Composes with the owner selector and the subtree
	///     scope, so "who is working what inside this subtree" is the three filters together. Settable
	///     for the same remembered-choice reason as <see cref="OwnerUserId" />.
	/// </summary>
	[BindProperty(SupportsGet = true)]
	public long? ActiveWorkerUserId { get; set; }

	// Settable so LoadAsync can replace an omitted value with the actor's home node (see LoadAsync),
	// which every replayed filter/route value then reflects.
	[BindProperty(SupportsGet = true)] public long? SubtreeRootId { get; set; }

	/// <summary>
	///     Asks for the whole tree without having to name its root: an inbound shorthand that overrides
	///     <see cref="SubtreeRootId" /> with the root's own id during <see cref="LoadAsync" />. It is not
	///     a second kind of scope — once resolved, the dashboard is scoped to the root node like any
	///     other, which is what the scope line names and the toolbar's Browse button opens.
	/// </summary>
	[BindProperty(SupportsGet = true)]
	public bool ShowWholeTree { get; set; }

	/// <summary>
	///     When non-blank, restricts to leaves whose description contains this text (case insensitive) —
	///     scopes the same owner/subtree-filtered candidate set as the rest of this dashboard's filters,
	///     unlike Browse's Search flow which queries the whole tree.
	/// </summary>
	[BindProperty(SupportsGet = true)]
	[Display(Name = "Search text")]
	public string? SearchText { get; set; }

	[TempData] public string? ErrorMessage { get; set; }

	[TempData] public string? SuccessMessage { get; set; }

	public JobNodeDetailResult? SubtreeRoot { get; private set; }

	/// <summary>
	///     The actor's own home node, if they have one — both the scope a node-less visit defaults to
	///     and the target of the "Show home node" link the scope line offers whenever the dashboard is
	///     looking somewhere else.
	/// </summary>
	public long? HomeNodeId { get; private set; }

	/// <summary>
	///     Whether the scope line should offer the way back to <see cref="HomeNodeId" />: only when the
	///     actor has a home node and the dashboard is not already showing exactly that subtree.
	/// </summary>
	public bool OffersHomeNodeScope => HomeNodeId.HasValue && SubtreeRootId != HomeNodeId;

	/// <summary>
	///     The subtree the query is restricted to — the scope node, except when that node is the tree's
	///     root: "every leaf under the root" and "every leaf" are the same set, so the filter is dropped
	///     rather than asking the query port to plan a containment test that excludes nothing.
	/// </summary>
	private JobNodeId? ScopeFilterRootId =>
		SubtreeRoot is not null && SubtreeRoot.Node.ParentId is not null ? SubtreeRoot.Node.Id : null;

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
	///     The same employee list as <see cref="OwnerOptions" />, built separately rather than shared:
	///     the <c>asp-items</c> tag helper marks <see cref="SelectListItem.Selected" /> on the items it
	///     is given, so one list bound to two selects would carry the owner choice into the
	///     active-worker one.
	/// </summary>
	public List<SelectListItem> ActiveWorkerOptions { get; private set; } = [];

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
		["ExcludeBlocked"] = ExcludeBlocked.ToString(),
		["InProgressOnly"] = InProgressOnly.ToString(),
		["ActiveWorkerUserId"] = ActiveWorkerUserId?.ToString(CultureInfo.InvariantCulture),
		["Offset"] = Offset.ToString(CultureInfo.InvariantCulture),
	};

	/// <summary>
	///     This exact dashboard view (owner/pool/subtree filters, page offset) as a URL, so
	///     <c>/Jobs/Work</c>'s Sessions link can return here on a successful ending action rather than
	///     defaulting to Browse rooted at the leaf (<see cref="WorkModel.ReturnUrl" />).
	/// </summary>
	public string? AwaitingProgressReturnUrl => Url.Page("/Jobs/AwaitingProgress", new
	{
		ownerUserId = OwnerUserId,
		unassignedOnly = UnassignedOnly,
		subtreeRootId = SubtreeRootId,
		showWholeTree = ShowWholeTree,
		searchText = SearchText,
		excludeBlocked = ExcludeBlocked,
		inProgressOnly = InProgressOnly,
		activeWorkerUserId = ActiveWorkerUserId,
		offset = Offset,
	});

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
		ReturnUrl = AwaitingProgressReturnUrl,
		CompleteHandler = "Complete",
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
		var actor = await userManager.GetAppUserIdAsync(User);
		if (actor is null) {
			return Challenge();
		}

		await LoadAsync(actor.Value, cancellationToken);
		return Page();
	}

	public async Task<IActionResult> OnPostStartWorkAsync(long jobNodeId, string? startedAt, CancellationToken cancellationToken)
	{
		var actor = await userManager.GetAppUserIdAsync(User);
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
	///     One-click "Complete": closes every currently active session on <paramref name="jobNodeId" />
	///     and marks it <see cref="Achievement.Success" /> through the same atomic composite as Browse.
	///     The version and active-session set are read immediately before the command because this
	///     compact row action offers no intervening review screen.
	/// </summary>
	public async Task<IActionResult> OnPostCompleteAsync(long jobNodeId, CancellationToken cancellationToken)
	{
		var actor = await userManager.GetAppUserIdAsync(User);
		if (actor is null) {
			return Challenge();
		}

		var context = new CommandContext { Actor = actor.Value, CorrelationId = Guid.NewGuid() };

		try {
			var leafNodeId = new JobNodeId(jobNodeId);
			var leafWork = await jobTrackClient.Query.GetLeafWorkAsync(
				new() { Context = context, JobNodeId = leafNodeId }, cancellationToken);
			var activeSessions = await jobTrackClient.Query.GetActiveSessionsAsync(
				new() { Context = context, LeafWorkIds = [leafNodeId] }, cancellationToken);

			var result = await jobTrackClient.Work.CompleteLeafAsync(new() {
				Context = context,
				JobNodeId = leafNodeId,
				Version = leafWork.Version,
				ExpectedActiveSessions =
					[.. activeSessions.Select(session => new ExpectedActiveSession { Id = session.Id, Version = session.Version })],
			}, cancellationToken);
			SuccessMessage = result.FinishedSessions.Count switch {
				0 => "Job marked complete.",
				1 => "Job marked complete. Its one open session was closed.",
				var count => $"Job marked complete. Its {count} open sessions were closed.",
			};
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "This leaf has no work attached.";
		}
		catch (ConcurrencyConflictException ex) {
			PageFailureLogging.LogConcurrencyConflict(logger, context.CorrelationId, nameof(AwaitingProgressModel), ex);
			ErrorMessage = "Someone else changed this leaf, or one of its active sessions, since the page was loaded.";
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = WorkSessionFailureDisplay.Describe(ex);
		}
		catch (PrerequisiteBlockedException) {
			ErrorMessage = "This job is blocked: a prerequisite it depends on is not complete, so it cannot be closed yet.";
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
		var actor = await userManager.GetAppUserIdAsync(User);
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
		["excludeBlocked"] = ExcludeBlocked,
		["inProgressOnly"] = InProgressOnly,
		["activeWorkerUserId"] = ActiveWorkerUserId,
		["offset"] = Offset,
	};

	/// <summary>
	///     Replaces each omitted filter with the choice this session last made, and remembers each one
	///     the request did carry. The subtree scope pair is not among them — see the note on the memory
	///     keys above, and the home-node default in <see cref="LoadAsync" />.
	/// </summary>
	private void RecallFilters()
	{
		var session = new CookieFilterMemoryStore(HttpContext, dataProtectionProvider);

		// The whole tree is browsable, so the owner default when nothing is remembered is "Everyone"
		// (no permission-scoped fallback like Work's).
		OwnerUserId = FilterMemory.Resolve(
			session, OwnerFilterSessionKey, Request.Query.ContainsKey(nameof(OwnerUserId)), OwnerUserId, null);
		UnassignedOnly = FilterMemory.ResolveFlag(
			session, UnassignedOnlyFilterSessionKey, Request.Query.ContainsKey(nameof(UnassignedOnly)), UnassignedOnly);
		SearchText = FilterMemory.ResolveText(
			session, SearchTextFilterSessionKey, Request.Query.ContainsKey(nameof(SearchText)), SearchText);
		ExcludeBlocked = FilterMemory.ResolveFlag(
			session, ExcludeBlockedFilterSessionKey, Request.Query.ContainsKey(nameof(ExcludeBlocked)), ExcludeBlocked);
		InProgressOnly = FilterMemory.ResolveFlag(
			session, InProgressOnlyFilterSessionKey, Request.Query.ContainsKey(nameof(InProgressOnly)), InProgressOnly);
		ActiveWorkerUserId = FilterMemory.Resolve(
			session, ActiveWorkerFilterSessionKey, Request.Query.ContainsKey(nameof(ActiveWorkerUserId)), ActiveWorkerUserId, null);
	}

	private async Task LoadAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		CurrentActorId = actor;
		ViewerZone = await viewerTimeZoneResolver.ResolveAsync(actor, cancellationToken);
		var context = new CommandContext { Actor = actor, CorrelationId = Guid.NewGuid() };

		RecallFilters();

		var directory = await jobTrackClient.Query.GetEmployeeDirectoryAsync(
			new() { Context = context }, cancellationToken);
		_employeeDirectoryById = directory.ToDictionary(entry => entry.Id);
		OwnerOptions = EmployeeDirectoryDisplay.BuildOptions(directory, new SelectListItem("Everyone", string.Empty));
		ActiveWorkerOptions = EmployeeDirectoryDisplay.BuildOptions(directory, new SelectListItem("Everyone", string.Empty));
		StartForWorkerOptions = EmployeeDirectoryDisplay.BuildOptions(directory);

		// The home node is loaded on every request, not just the ones that fall back to it: the scope
		// line offers it as a way back whenever the dashboard is looking elsewhere. A visit naming no
		// node -- the header nav link, a hand-typed address -- scopes to it rather than to the entire
		// tree.
		var profile = await jobTrackClient.Query.GetEmployeeProfileAsync(
			new() { Context = context, TargetUserId = actor }, cancellationToken);
		HomeNodeId = profile.HomeNodeId?.Value;
		SubtreeRootId ??= HomeNodeId;

		// The dashboard is always scoped to exactly one node, named on the page and opened by the
		// toolbar's Browse button. "The whole tree" is not a scope of its own, just that node being the
		// tree's root: ShowWholeTree (and the no-home-node fallback) resolves to the root here, with a
		// null NodeId asking for it by name, so every downstream use -- the scope line, the Browse
		// button, the replayed route values -- names one real node.
		var requestedNodeId = !ShowWholeTree && SubtreeRootId.HasValue ? new JobNodeId(SubtreeRootId.Value) : (JobNodeId?)null;

		try {
			SubtreeRoot = await jobTrackClient.Query.GetJobNodeAsync(
				new() { Context = context, NodeId = requestedNodeId }, cancellationToken);
			SubtreeRootId = SubtreeRoot.Node.Id.Value;

			var ownership = (UnassignedOnly, OwnerUserId) switch {
				(true, _) => OwnershipFilter.Unassigned,
				(false, long ownerUserId) => OwnershipFilter.OwnedBy(new(ownerUserId)),
				(false, null) => OwnershipFilter.All,
			};

			var page = await jobTrackClient.Query.GetAwaitingProgressAsync(
				new() {
					Context = context,
					Ownership = ownership,
					SubtreeRootId = ScopeFilterRootId,
					SearchText = SearchText,
					ExcludeBlocked = ExcludeBlocked,
					InProgressOnly = InProgressOnly,
					ActiveWorkerUserId = ActiveWorkerUserId.HasValue ? new AppUserId(ActiveWorkerUserId.Value) : null,
					Offset = Math.Max(0, Offset),
					Limit = PageSize + 1,
				},
				cancellationToken);

			HasMore = page.Count > PageSize;
			Entries = HasMore ? [.. page.Take(PageSize)] : page;

			await LoadActiveSessionsAsync(context, cancellationToken);
		}
		catch (EntityNotFoundException) {
			// A named node that does not resolve is worth saying out loud; a tree with no root at all
			// (nothing bootstrapped yet) is not an error the viewer made, so the page just lists nothing.
			if (requestedNodeId.HasValue) {
				ErrorMessage = "That job node does not exist.";
			}
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
}
