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
///     Job-tree browsing, search, ownership and archive filters, readiness explanations (plan §8.5
///     slice 2), and inline session start/finish on each leaf row (recording work is the app's most
///     common action, so it does not require navigating to <see cref="WorkModel" /> first). Viewing job
///     data carries no ownership-based authorization gate (spec §7.3), so the page uses the broad "any
///     employee" policy: any signed-in employee role may browse; the inline start/finish handlers carry
///     no additional page-level policy either, matching <see cref="WorkModel" /> — the commands
///     themselves re-evaluate <see cref="Domain.Authorization.WorkSessionAccessPolicy" /> per call.
///     Readiness is fetched only for the single currently displayed node, and the active-session
///     indicator is fetched once for every leaf row in one batched call, never per row
///     (<see cref="JobNodeSummaryResult" />: "no per-row N+1 readiness lookups").
/// </summary>
[Authorize(Policy = JobTrackPolicyNames.AnyEmployee)]
public sealed partial class BrowseModel(
	IJobTrackClient jobTrackClient,
	UserManager<JobTrackIdentityUser> userManager,
	IViewerTimeZoneResolver viewerTimeZoneResolver,
	IClock clock,
	IDataProtectionProvider dataProtectionProvider,
	ILogger<BrowseModel> logger)
	: PageModel
{
	// Browse-sessions filter memory: the owner selector's last "person or All owners" choice is
	// remembered per session under this key so returning to Browse (any node) restores it.
	private const string OwnerFilterSessionKey = "Jobs.Browse.Owner";

	// Mirrors OwnerFilterSessionKey for the subtree "Show archived" toggle -- the last choice is
	// remembered per session so navigating to a different node (or back later) keeps it rather than
	// silently reverting to active-only.
	private const string ShowArchivedSessionKey = "Jobs.Browse.ShowArchived";

	// The last node actually browsed (not a search view), remembered per session so the search
	// flow's "Browse" button can return to it -- the search form itself carries no node id, so
	// without this the only way back would be resetting to the root. An empty stored string means
	// "the root", matching FilterMemory's own convention; an absent key means nothing browsed yet
	// this session.
	private const string LastBrowsedNodeSessionKey = "Jobs.Browse.LastNode";
	private const string RecentNodeIdsSessionKey = "Jobs.Browse.RecentNodeIds";
	private const int RecentNodeCap = 20;
	internal const int RecentNodeDescriptionLength = 30;
	private EquatableArray<EmployeeDirectoryEntry> _employeeDirectory = [];
	private IFilterMemoryStore? filterMemoryStore;

	/// <summary>
	///     Built once per request (and reused across this page's several filter-memory call sites)
	///     rather than in the primary constructor, since <see cref="PageModel.HttpContext" /> is not
	///     available until a handler runs.
	/// </summary>
	private IFilterMemoryStore FilterMemoryStore => filterMemoryStore ??= new CookieFilterMemoryStore(HttpContext, dataProtectionProvider);

	/// <summary>Captured once per request, per ADR 0016's "one captured instant per operation".</summary>
	public Instant Now { get; } = clock.GetCurrentInstant();

	/// <summary>
	///     The node to root the browser at. Settable so <see cref="LoadAsync" /> can substitute
	///     <see cref="HomeNodeId" /> when the request named no node at all (the header's "Jobs" link);
	///     an explicit id — the root's own included, which the breadcrumb's root link carries — always
	///     wins, and <see langword="null" /> after that resolution means the tree root.
	/// </summary>
	[BindProperty(SupportsGet = true)]
	public long? NodeId { get; set; }

	// Settable so LoadAsync can replace an omitted value with the remembered choice (browse-sessions
	// filter memory); the owner <select> (asp-for) and every replayed filter/route value then reflect it.
	[BindProperty(SupportsGet = true)] public long? OwnerUserId { get; set; }

	/// <summary>
	///     When set, overrides <see cref="OwnerUserId" /> to show only the unassigned pool
	///     (ownership model §2.1) — the two are mutually exclusive filter shapes
	///     <see cref="OwnershipFilter" /> exists to keep distinct.
	/// </summary>
	[BindProperty(SupportsGet = true)]
	public bool UnassignedOnly { get; init; }

	[BindProperty(SupportsGet = true)]
	[Display(Name = "Archive filter")]
	public JobArchiveFilter ArchiveFilter { get; init; } = JobArchiveFilter.ActiveOnly;

	/// <summary>
	///     The subtree view's own "Show archived" toggle -- unlike <see cref="ArchiveFilter" /> (which
	///     scopes the Search flow's whole-tree query), this genuinely scopes the currently browsed
	///     subtree, so it stays on Browse rather than moving out with the rest of the filter box. A
	///     single toggle only distinguishes "active only" from "everything" — <see cref="JobArchiveFilter.ArchivedOnly" />
	///     stays a Search-only refinement. Settable so <see cref="LoadAsync" /> can replace an omitted
	///     value with the remembered choice (<see cref="ShowArchivedSessionKey" />), matching
	///     <see cref="OwnerUserId" />'s own per-session filter memory.
	/// </summary>
	[BindProperty(SupportsGet = true)]
	public bool ShowArchived { get; set; }

	[BindProperty(SupportsGet = true)]
	[Display(Name = "Search text")]
	public string? SearchText { get; init; }

	/// <summary>
	///     Set by the toolbar's "Search" link to land on the blank search form
	///     (<see cref="ShowSearchEntry" />) rather than the root node's tree view — search spans the
	///     whole job tree (<see cref="IJobQueries.SearchJobNodesAsync" /> carries no subtree scoping), so
	///     it is a distinct entry point from browsing, not a filter layered onto a node.
	/// </summary>
	[BindProperty(SupportsGet = true)]
	public bool Search { get; init; }

	[TempData] public string? ErrorMessage { get; set; }

	[TempData] public string? SuccessMessage { get; set; }

	[TempData] public bool RecentHistoryWasCleared { get; set; }

	public JobNodeDetailResult? CurrentNode { get; private set; }

	/// <summary>
	///     Recently visited nodes resolved afresh through the authorized query boundary from the
	///     principal-bound, protected identifier list. Descriptions never enter browser-local storage.
	/// </summary>
	public EquatableArray<JobNodeSummaryResult> RecentNodes { get; private set; } = [];

	/// <summary>
	///     Requester context for the currently displayed node, if it has an associated
	///     <c>job_request</c> row and the caller is authorized to view it (<see cref="Domain.Authorization.RequesterAccessPolicy" />)
	///     — staff triaging a holding-area queue need to identify a requester-submitted job without
	///     navigating away (plan §5, §9 Stage 5). <see langword="null" /> for an ordinary job node, or when
	///     the viewer does not control it.
	/// </summary>
	public JobRequestDetailResult? RequestContext { get; private set; }

	public EquatableArray<JobNodeSummaryResult> Children { get; private set; } = [];

	/// <summary>
	///     The bounded multi-level subtree rooted at <see cref="CurrentNode" /> (ADR 0039), replacing the
	///     single-level <see cref="Children" /> listing outside search mode -- <see langword="null" />
	///     during search, where results stay a flat <see cref="Children" /> match list (a search result
	///     set isn't a rooted subtree, so ADR 0039's depth/breadth bounds and interval span don't apply).
	/// </summary>
	public JobSubtreeResult? Subtree { get; private set; }

	/// <summary>
	///     Every node of <see cref="Subtree" /> except the root itself, in pre-order render order (ADR
	///     0039 decision 3: <c>SubtreeLft</c> ordering, not the port's <c>Id</c> ordering) -- the rows
	///     the tree table renders.
	/// </summary>
	public IReadOnlyList<JobSubtreeNodeResult> SubtreeDescendants =>
		Subtree is null ? [] : [.. Subtree.Nodes.Where(n => n.Id != Subtree.RootId).OrderBy(n => n.SubtreeLft)];

	public ReadinessResult? Readiness { get; private set; }

	/// <summary>
	///     Ids of the direct prerequisites currently blocking the displayed node — the subset of
	///     <see cref="Requires" /> whose id appears among <see cref="ReadinessResult.Blockers" />. The
	///     prerequisites list tags each of these as blocking and the rest as satisfied, which is why the
	///     standalone readiness panel is gone: a red/green marker per prerequisite carries the same fact
	///     without repeating the node list. A blocker declared on an ancestor (not a direct edge of this
	///     node) is itemised separately in <see cref="AncestorBlockers" />.
	/// </summary>
	public IReadOnlySet<JobNodeId> BlockingRequiredIds =>
		Readiness is null ? new() : Readiness.Blockers.Select(b => b.RequiredJobId).ToHashSet();

	/// <summary>
	///     Unsatisfied prerequisites inherited from an ancestor (spec §6: readiness aggregates
	///     prerequisites declared on the node <em>and every ancestor</em>) — i.e. every
	///     <see cref="ReadinessResult.Blockers" /> entry whose <see cref="UnsatisfiedPrerequisite.DeclaredOnJobId" />
	///     is not this node itself. These are not among this node's own <see cref="Requires" /> edges, so
	///     the per-prerequisite markers can't show them; itemising them here (blocking job + which
	///     ancestor declared the edge) keeps the "why is this blocked" story complete after the standalone
	///     readiness panel was removed.
	/// </summary>
	public IReadOnlyList<AncestorBlockerView> AncestorBlockers { get; private set; } = [];

	public EquatableArray<JobNodeSummaryResult> Requires { get; private set; } = [];

	public EquatableArray<JobNodeSummaryResult> RequiredBy { get; private set; } = [];

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

	/// <summary>The signed-in actor's own time zone, for formatting every timestamp on this page (<see cref="InstantDisplay" />).</summary>
	public DateTimeZone ViewerZone { get; private set; } = DateTimeZoneProviders.Tzdb["Etc/UTC"];

	/// <summary>
	///     The current node's recorded achievement, when it is a leaf with work attached. Read through
	///     the existing leaf-work query rather than threaded onto <see cref="JobNodeResult" />, which
	///     every job-node command path also projects — one extra read on a leaf's own detail page is
	///     cheaper than that blast radius. <see langword="null" /> for a branch, or a leaf without work.
	/// </summary>
	public Achievement? CurrentNodeAchievement { get; private set; }

	/// <summary>
	///     The current node's rollup achievement, derived from its complete descendant subtree, when it
	///     is a branch or the root. <see langword="null" /> for a leaf, where
	///     <see cref="CurrentNodeAchievement" /> renders instead — the two are mutually exclusive.
	/// </summary>
	public BranchAchievement? CurrentNodeBranchAchievement { get; private set; }

	/// <summary>
	///     Whether the current node has yet to reach a terminal state — a branch none of whose subtree
	///     has finished succeeding, or a leaf that has not ended in success, cancellation or failure (a
	///     leaf with no work attached at all has not started, so it is open too). Only an open job's
	///     passed deadline is worth colouring red; a closed one's is a matter of record.
	/// </summary>
	public bool CurrentNodeIsOpen =>
		CurrentNodeBranchAchievement is BranchAchievement branchAchievement
			? branchAchievement is BranchAchievement.Unfinished
			: CurrentNodeAchievement is not (Achievement.Success or Achievement.Cancelled or Achievement.Unsuccessful);

	/// <summary>
	///     The current leaf's Sessions panel (shared with <see cref="WorkModel" /> via
	///     <c>_LeafWorkSessions</c>) — <see langword="null" /> for a branch/root, where the subtree table
	///     renders instead (a node never has both children and leaf work, so the two are mutually
	///     exclusive), or when the leaf has no work attached yet.
	/// </summary>
	public LeafWorkSessionsPanelModel? Panel { get; private set; }

	/// <summary>
	///     Every enabled workflow employee's directory entry, keyed by id, for resolving an
	///     owner's display name/username instead of showing a bare <see cref="AppUserId" /> (see
	///     <see cref="IJobQueries.GetEmployeeDirectoryAsync" />). An owner id absent from this
	///     dictionary (disabled or role-revoked since assignment) falls back to showing the raw id.
	/// </summary>
	public IReadOnlyDictionary<AppUserId, EmployeeDirectoryEntry> EmployeeDirectoryById { get; private set; } =
		new Dictionary<AppUserId, EmployeeDirectoryEntry>();

	/// <summary>
	///     Options for the owner filter <c>&lt;select&gt;</c>: an "All owners" default (empty
	///     value, clearing <see cref="OwnerUserId" /> back to <see cref="OwnershipFilter.All" />) followed
	///     by every workflow employee as "display name (username)" — never a raw numeric id.
	/// </summary>
	public IReadOnlyList<SelectListItem> OwnerFilterOptions { get; private set; } = [];

	/// <summary>Every enabled workflow employee, for the "Start for…" worker picker (plan §2.5).</summary>
	public IReadOnlyList<SelectListItem> StartForWorkerOptions { get; private set; } = [];

	/// <summary>
	///     The current actor's configured home node (see <see cref="EmployeeProfileResult.HomeNodeId" />),
	///     for showing/hiding the "Set as home node"/"Reset to root" toolbar actions below.
	/// </summary>
	public JobNodeId? HomeNodeId { get; private set; }

	/// <summary>
	///     The node the viewer was browsing before opening Search (<see cref="LastBrowsedNodeSessionKey" />),
	///     for the search flow's "Browse" button — the last node actually browsed if session remembers
	///     one, else <see cref="HomeNodeId" />, else <see langword="null" /> (the root). Only populated
	///     while <see cref="IsSearch" /> or <see cref="ShowSearchEntry" />.
	/// </summary>
	public JobNodeId? SearchOriginNodeId { get; private set; }

	public bool IsSearch => !string.IsNullOrWhiteSpace(SearchText);

	/// <summary>
	///     The blank search form reached via the toolbar's "Search" link before any
	///     <see cref="SearchText" /> has been entered — mutually exclusive with <see cref="IsSearch" />,
	///     which takes over once the form is submitted.
	/// </summary>
	public bool ShowSearchEntry => Search && !IsSearch;

	/// <summary>
	///     The page's own view state, replayed as hidden fields by every per-row work form so a start
	///     or finish lands back on the same node, owner filter, archive filter, and search rather than
	///     resetting the browser to the root.
	/// </summary>
	public IReadOnlyDictionary<string, string?> RowStateFields => new Dictionary<string, string?> {
		["NodeId"] = NodeId?.ToString(CultureInfo.InvariantCulture),
		["OwnerUserId"] = OwnerUserId?.ToString(CultureInfo.InvariantCulture),
		["UnassignedOnly"] = UnassignedOnly.ToString(CultureInfo.InvariantCulture),
		["ArchiveFilter"] = ArchiveFilter.ToString(),
		["SearchText"] = SearchText,
		["ShowArchived"] = ShowArchived.ToString(CultureInfo.InvariantCulture),
	};

	/// <summary>
	///     This exact Browse view (rooted node, owner/archive/search filters) as a URL, so
	///     <c>/Jobs/Work</c>'s Sessions link can return here on a successful ending action rather than
	///     defaulting to Browse rooted at the leaf (<see cref="WorkModel.ReturnUrl" />).
	/// </summary>
	public string? BrowseReturnUrl => Url.Page("/Jobs/Browse", new
	{
		nodeId = NodeId,
		ownerUserId = OwnerUserId,
		unassignedOnly = UnassignedOnly,
		archiveFilter = ArchiveFilter,
		searchText = SearchText,
		showArchived = ShowArchived,
	});

	/// <summary>
	///     Whether one child-table node is still open. Branches use their recursive two-state roll-up;
	///     leaves use the terminal states from the separate leaf achievement vocabulary.
	/// </summary>
	public static bool SubtreeNodeIsOpen(JobSubtreeNodeResult node)
	{
		ArgumentNullException.ThrowIfNull(node);
		return node.BranchAchievement is BranchAchievement branchAchievement
			? branchAchievement is BranchAchievement.Unfinished
			: node.Achievement is not (Achievement.Success or Achievement.Cancelled or Achievement.Unsuccessful);
	}

	/// <summary>
	///     Builds a <see cref="WorkRowActionsModel" /> for <paramref name="leafId" />, sourcing its active-session collection and manage capability
	///     from the batched loads above.
	/// </summary>
	public WorkRowActionsModel WorkRowActionsFor(
		JobNodeId leafId, string startHandler, string startNodeFieldName, Achievement? achievement, bool isArchived,
		bool startForLabelled = false) => new() {
			LeafNodeId = leafId.Value,
			ViewerId = CurrentActorId ?? new AppUserId(0),
			ActiveSessions = ActiveSessionsByLeaf.GetValueOrDefault(leafId, []),
			CanManage = CanManageByLeaf.GetValueOrDefault(leafId, false),
			Achievement = achievement,
			IsArchived = isArchived,
			ViewerZone = ViewerZone,
			StartHandler = startHandler,
			StartNodeFieldName = startNodeFieldName,
			PageStateFields = RowStateFields,
			StartForWorkerOptions = StartForWorkerOptions,
			StartForLabelled = startForLabelled,
			ReturnUrl = BrowseReturnUrl,
			CompleteHandler = "Complete",
		};

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadAsync(actor.Value, cancellationToken);
		return Page();
	}

	public async Task<IActionResult> OnPostClearRecentHistoryAsync()
	{
		if (await ResolveActorAsync() is null) {
			return Challenge();
		}

		FilterMemoryStore.SetString(RecentNodeIdsSessionKey, string.Empty);
		RecentHistoryWasCleared = true;
		return RedirectToPage(CurrentRouteValues());
	}

	public async Task<IActionResult> OnPostStartAsync(long leafNodeId, string? startedAt, CancellationToken cancellationToken)
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

			_ = await jobTrackClient.Work.StartWorkAsync(new() {
				Context = new() {
					Actor = actor.Value,
					CorrelationId = Guid.NewGuid(),
				},
				JobNodeId = new(leafNodeId),
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
	///     (plan §2.5 "Starting for another worker") — a distinct handler/field from
	///     <see cref="OnPostStartAsync" /> so the "Start for…" disclosure can never be confused with the
	///     one-click Start. <see cref="StartForDisclosureModel.StartForFieldName" /> is a mutation
	///     target, distinct from any session-history filter. Authorization is not
	///     rechecked here beyond signing in — <c>StartWorkAsync</c> itself re-evaluates
	///     <see cref="Domain.Authorization.WorkSessionAccessPolicy.CanManage" /> for the acting user
	///     against this leaf and rejects an unauthorized actor with <see cref="AuthorizationDeniedException" />.
	/// </summary>
	public async Task<IActionResult> OnPostStartForAsync(
		long leafNodeId, long? startForUserId, string? startedAt, CancellationToken cancellationToken)
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

			_ = await jobTrackClient.Work.StartWorkAsync(new() {
				Context = new() {
					Actor = actor.Value,
					CorrelationId = Guid.NewGuid(),
				},
				JobNodeId = new(leafNodeId),
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

	/// <summary>"Pause work" from the leaf detail view — mirrors <see cref="WorkModel.OnPostFinishAsync" />.</summary>
	public async Task<IActionResult> OnPostFinishAsync(
		long sessionId, long version, string? finishedAt, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		var context = new CommandContext {
			Actor = actor.Value,
			CorrelationId = Guid.NewGuid(),
		};

		try {
			var zone = await viewerTimeZoneResolver.ResolveAsync(actor.Value, cancellationToken);
			if (!BackdateInstant.TryParseOptional(finishedAt, zone, out var finishedAtInstant)) {
				ErrorMessage = "Enter a valid date and time.";
				return RedirectToPage(CurrentRouteValues());
			}

			_ = await jobTrackClient.Work.FinishSessionAsync(new() {
				Context = context,
				SessionId = new(sessionId),
				Version = version,
				FinishedAt = finishedAtInstant,
			}, cancellationToken);
			SuccessMessage = "Ends this session; the job stays In Progress.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That session does not exist.";
		}
		catch (ConcurrencyConflictException ex) {
			PageFailureLogging.LogConcurrencyConflict(logger, context.CorrelationId, nameof(BrowseModel), ex);
			ErrorMessage = "Someone else changed this session since the page was loaded. The list below is refreshed.";
		}
		catch (InvariantViolationException ex) {
			ErrorMessage = WorkSessionFailureDisplay.Describe(ex);
		}

		return RedirectToPage(CurrentRouteValues());
	}

	/// <summary>
	///     One-click "Complete": closes every currently active session on <paramref name="leafNodeId" />
	///     and marks it <see cref="Achievement.Success" />, in the one atomic
	///     <see cref="IWorkCommands.CompleteLeafAsync" /> composite <c>/Jobs/Work</c>'s own Complete
	///     button uses. Unlike that page, this button offers no intervening review of which sessions will
	///     close, so the active-session set and leaf version are read fresh here rather than replayed
	///     from a page render the actor may have had open a while — the narrowest possible window for a
	///     concurrent start/finish to race the click, reported as a conflict rather than silently
	///     swept in or excluded.
	/// </summary>
	public async Task<IActionResult> OnPostCompleteAsync(long leafNodeId, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		var context = new CommandContext {
			Actor = actor.Value,
			CorrelationId = Guid.NewGuid(),
		};

		try {
			var jobNodeId = new JobNodeId(leafNodeId);

			var leafWork = await jobTrackClient.Query.GetLeafWorkAsync(
				new() {
					Context = context,
					JobNodeId = jobNodeId,
				}, cancellationToken);
			var activeSessions = await jobTrackClient.Query.GetActiveSessionsAsync(
				new() {
					Context = context,
					LeafWorkIds = [jobNodeId],
				}, cancellationToken);

			var result = await jobTrackClient.Work.CompleteLeafAsync(new() {
				Context = context,
				JobNodeId = jobNodeId,
				Version = leafWork.Version,
				ExpectedActiveSessions = [
					.. activeSessions.Select(session => new ExpectedActiveSession {
						Id = session.Id, Version = session.Version,
					}),
				],
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
			PageFailureLogging.LogConcurrencyConflict(logger, context.CorrelationId, nameof(BrowseModel), ex);
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
	///     Claims <paramref name="pickUpNodeId" /> — the node whose row was clicked, which is not
	///     generally the node being browsed. The parameter is deliberately not named <c>nodeId</c>:
	///     model binding is case-insensitive, so it would bind from the same posted value as this
	///     page's own <see cref="NodeId" /> browsing state (which every form replays as a hidden
	///     field) and claim whatever node the viewer happened to be looking at instead.
	/// </summary>
	public async Task<IActionResult> OnPostPickUpAsync(long pickUpNodeId, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		try {
			_ = await jobTrackClient.Jobs.PickUpAsync(
				new() {
					Context = new() {
						Actor = actor.Value,
						CorrelationId = Guid.NewGuid(),
					},
					NodeId = new(pickUpNodeId),
				},
				cancellationToken);
			SuccessMessage = "Job node claimed.";
		}
		catch (AuthorizationDeniedException) {
			return Forbid();
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node does not exist.";
		}
		catch (InvariantViolationException) {
			ErrorMessage = "This job node has already been claimed by someone else.";
		}

		return RedirectToPage(CurrentRouteValues());
	}

	/// <summary>
	///     Pins <paramref name="homeNodeId" /> as the actor's home node. Named apart from
	///     <see cref="NodeId" /> for the same binding reason as <see cref="OnPostPickUpAsync" />.
	/// </summary>
	public async Task<IActionResult> OnPostSetHomeNodeAsync(long homeNodeId, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await SetHomeNodeAsync(actor.Value, new JobNodeId(homeNodeId), cancellationToken);
		return RedirectToPage(CurrentRouteValues());
	}

	public async Task<IActionResult> OnPostResetHomeNodeAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await SetHomeNodeAsync(actor.Value, null, cancellationToken);
		return RedirectToPage(CurrentRouteValues());
	}

	private async Task SetHomeNodeAsync(AppUserId actor, JobNodeId? nodeId, CancellationToken cancellationToken)
	{
		try {
			_ = await jobTrackClient.Employees.SetHomeNodeAsync(
				new() {
					Context = new() {
						Actor = actor,
						CorrelationId = Guid.NewGuid(),
					},
					NodeId = nodeId,
				}, cancellationToken);
			SuccessMessage = nodeId is null ? "Home node reset to root." : "Home node set.";
		}
		catch (InvariantViolationException) {
			ErrorMessage = "A leaf cannot be set as a home node.";
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node does not exist.";
		}
	}

	/// <summary>
	///     The page's own browsing context (mirrors <see cref="RowStateFields" />, minus the string
	///     conversion), replayed on the redirect every mutating handler ends with so the reloaded GET
	///     lands back on the same node, owner filter, archive filter, and search rather than resetting
	///     to the root.
	/// </summary>
	private RouteValueDictionary CurrentRouteValues() => new() {
		["nodeId"] = NodeId,
		["ownerUserId"] = OwnerUserId,
		["unassignedOnly"] = UnassignedOnly,
		["archiveFilter"] = ArchiveFilter,
		["searchText"] = SearchText,
		["showArchived"] = ShowArchived,
	};
}
