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
public sealed class BrowseModel(
	IJobTrackClient jobTrackClient,
	UserManager<JobTrackIdentityUser> userManager,
	IViewerTimeZoneResolver viewerTimeZoneResolver,
	IClock clock)
	: PageModel
{
	// Browse-sessions filter memory: the owner selector's last "person or All owners" choice is
	// remembered per session under this key so returning to Browse (any node) restores it.
	private const string OwnerFilterSessionKey = "Jobs.Browse.Owner";
	private EquatableArray<EmployeeDirectoryEntry> _employeeDirectory = [];

	/// <summary>Captured once per request, per ADR 0016's "one captured instant per operation".</summary>
	public Instant Now { get; } = clock.GetCurrentInstant();

	[BindProperty(SupportsGet = true)] public long? NodeId { get; init; }

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

	[BindProperty(SupportsGet = true)] public JobArchiveFilter ArchiveFilter { get; init; } = JobArchiveFilter.ActiveOnly;

	[BindProperty(SupportsGet = true)] public string? SearchText { get; init; }

	[TempData] public string? ErrorMessage { get; set; }

	[TempData] public string? SuccessMessage { get; set; }

	public JobNodeDetailResult? CurrentNode { get; private set; }

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

	public bool IsSearch => !string.IsNullOrWhiteSpace(SearchText);

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
	};

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
		};

	/// <summary>
	///     <paramref name="node" />'s nested-set span (ADR 0039 decision 3) as a left offset and width
	///     percentage of the whole subtree's span, for the interval bar column — rebased so the root's
	///     own span always renders as the full-width track.
	/// </summary>
	public (decimal LeftPercent, decimal WidthPercent) SubtreeSpanPercent(JobSubtreeNodeResult node)
	{
		var root = Subtree!.Nodes.Single(n => n.Id == Subtree.RootId);
		var totalSpan = (decimal)Math.Max(root.SubtreeRgt - root.SubtreeLft, 1);
		var left = (node.SubtreeLft - root.SubtreeLft) * 100m / totalSpan;
		var width = Math.Max((node.SubtreeRgt - node.SubtreeLft) * 100m / totalSpan, 2m);
		return (left, width);
	}

	public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await LoadAsync(actor.Value, cancellationToken);
		return Page();
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
				Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
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
				Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
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
			SuccessMessage = "Ends this session; the job stays In Progress.";
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
				new() { Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() }, NodeId = new(pickUpNodeId) },
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
				new() { Context = new() { Actor = actor, CorrelationId = Guid.NewGuid() }, NodeId = nodeId }, cancellationToken);
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
	};

	private async Task LoadAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		CurrentActorId = actor;
		ViewerZone = await viewerTimeZoneResolver.ResolveAsync(actor, cancellationToken);
		var context = new CommandContext { Actor = actor, CorrelationId = Guid.NewGuid() };

		// Owner filter is remembered across visits (the whole tree is browsable, so the default when
		// nothing is remembered is "All owners"). UnassignedOnly still overrides it below when set.
		OwnerUserId = FilterMemory.Resolve(
			HttpContext.Session, OwnerFilterSessionKey, Request.Query.ContainsKey(nameof(OwnerUserId)), OwnerUserId, null);

		var ownerFilter = (UnassignedOnly, OwnerUserId) switch {
			(true, _) => OwnershipFilter.Unassigned,
			(false, long ownerUserId) => OwnershipFilter.OwnedBy(new(ownerUserId)),
			(false, null) => OwnershipFilter.All,
		};

		await LoadEmployeeDirectoryAsync(context, cancellationToken);
		await LoadHomeNodeAsync(context, actor, cancellationToken);

		try {
			if (IsSearch) {
				Children = await jobTrackClient.Query.SearchJobNodesAsync(new() {
					Context = context,
					SearchText = SearchText!,
					Ownership = ownerFilter,
					ArchiveFilter = ArchiveFilter,
				}, cancellationToken);

				await LoadActiveSessionsAsync(context, cancellationToken);
				return;
			}

			CurrentNode = await jobTrackClient.Query.GetJobNodeAsync(
				new() { Context = context, NodeId = NodeId.HasValue ? new JobNodeId(NodeId.Value) : null }, cancellationToken);

			Subtree = await jobTrackClient.Query.GetJobSubtreeAsync(new() {
				Context = context,
				RootId = CurrentNode.Node.Id,
				Ownership = ownerFilter,
				ArchiveFilter = ArchiveFilter,
				AsOf = Now,
			}, cancellationToken);

			Readiness = await jobTrackClient.Query.GetReadinessAsync(new() { Context = context, NodeId = CurrentNode.Node.Id }, cancellationToken);

			await LoadPrerequisitesAndDependentsAsync(context, CurrentNode.Node.Id, cancellationToken);
			await LoadAncestorBlockersAsync(context, CurrentNode, Readiness, cancellationToken);
			await LoadActiveSessionsAsync(context, cancellationToken);
			await LoadRequestContextAsync(context, CurrentNode.Node.Id, cancellationToken);

			if (CurrentNode.Node.Kind == NodeKind.Leaf) {
				await LoadCurrentNodeAchievementAsync(context, CurrentNode.Node, cancellationToken);
				await LoadLeafSessionsPanelAsync(context, CurrentNode.Node.Id, cancellationToken);
			}
		}
		catch (EntityNotFoundException) {
			ErrorMessage = "That job node does not exist.";
		}
	}

	private async Task LoadRequestContextAsync(CommandContext context, JobNodeId nodeId, CancellationToken cancellationToken)
	{
		try {
			RequestContext = await jobTrackClient.Requests.GetDetailAsync(new() { Context = context, NodeId = nodeId }, cancellationToken);
		}
		catch (InvariantViolationException ex) when (ex.ConstraintId == "requester-job-required") {
			RequestContext = null;
		}
		catch (AuthorizationDeniedException) {
			RequestContext = null;
		}
	}

	private async Task LoadActiveSessionsAsync(CommandContext context, CancellationToken cancellationToken)
	{
		// Inline start/finish renders on every visible leaf row across the whole rendered subtree,
		// not just level-1 children (plan §7: recording work is the most common action), so the
		// batched active-session lookup must cover every leaf the subtree fetch returned, not only
		// Children -- still one call, never per-row (JobNodeSummaryResult's own "no per-row N+1" rule).
		var leafIds = IsSearch
			? Children.Where(c => !c.HasChildren).Select(c => c.Id).ToArray()
			: Subtree?.Nodes.Where(n => !n.HasChildren).Select(n => n.Id).ToArray() ?? [];
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

		// Batched rendering hint for the "Start for..." disclosure and another worker's exact finish
		// (ADR 0044 Stage 4/6) -- one round trip regardless of leaf count, never re-derived per row.
		var capabilities = await jobTrackClient.Query.GetSessionManageCapabilitiesAsync(
			new() { Context = context, LeafWorkIds = [.. leafIds] }, cancellationToken);
		CanManageByLeaf = capabilities.ToDictionary(c => c.LeafWorkId, c => c.CanManage);
	}

	private async Task LoadEmployeeDirectoryAsync(CommandContext context, CancellationToken cancellationToken)
	{
		_employeeDirectory = await jobTrackClient.Query.GetEmployeeDirectoryAsync(
			new() { Context = context }, cancellationToken);

		EmployeeDirectoryById = _employeeDirectory.ToDictionary(entry => entry.Id);
		OwnerFilterOptions = EmployeeDirectoryDisplay.BuildOptions(_employeeDirectory, new SelectListItem("All owners", string.Empty));
		StartForWorkerOptions = EmployeeDirectoryDisplay.BuildOptions(_employeeDirectory);
	}

	private async Task LoadCurrentNodeAchievementAsync(CommandContext context, JobNodeResult node, CancellationToken cancellationToken)
	{
		if (!node.HasLeafWork) {
			return;
		}

		var leafWork = await jobTrackClient.Query.GetLeafWorkAsync(
			new() { Context = context, JobNodeId = node.Id }, cancellationToken);
		CurrentNodeAchievement = leafWork.Achievement;
	}

	/// <summary>
	///     Builds <see cref="Panel" /> for the current leaf, if it has work attached — mirrors
	///     <see cref="WorkModel.LoadAsync" />'s own panel construction, with <see cref="RowStateFields" />
	///     standing in for <see cref="WorkModel.ToolbarStateFields" /> as the redisplay/redirect state
	///     each row's forms replay. Unlike <see cref="WorkModel" />, Browse's leaf detail view always
	///     shows every worker's sessions — recorded work is job data every employee may read (ADR 0041),
	///     and a follow-up narrowing filter belongs on the dedicated Sessions page, not repeated here.
	/// </summary>
	private async Task LoadLeafSessionsPanelAsync(CommandContext context, JobNodeId leafId, CancellationToken cancellationToken)
	{
		if (!CurrentNode!.Node.HasLeafWork) {
			return;
		}

		try {
			var sessions = await jobTrackClient.Query.GetLeafSessionsAsync(
				new() { Context = context, LeafWorkId = leafId, WorkedByUserId = null }, cancellationToken);

			Panel = new() {
				LeafNodeId = leafId.Value,
				ViewerZone = ViewerZone,
				DisplayedWorkedByUserId = null,
				DisplayedWorkedByName = null,
				Sessions = sessions,
				EmployeeDirectoryById = EmployeeDirectoryById,
				WorkedByOptions = [],
				ShowWorkerFilter = false,
				ExtraHiddenFields = RowStateFields,
			};
		}
		catch (AuthorizationDeniedException) {
			ErrorMessage = "You may not view that worker's sessions on this leaf.";
		}
	}

	private async Task LoadHomeNodeAsync(CommandContext context, AppUserId actor, CancellationToken cancellationToken)
	{
		var profile = await jobTrackClient.Query.GetEmployeeProfileAsync(
			new() { Context = context, TargetUserId = actor }, cancellationToken);

		HomeNodeId = profile.HomeNodeId;
	}

	private async Task<AppUserId?> ResolveActorAsync()
	{
		var actor = await userManager.GetUserAsync(User);
		return actor?.AppUserId;
	}

	private async Task LoadPrerequisitesAndDependentsAsync(CommandContext context, JobNodeId nodeId, CancellationToken cancellationToken)
	{
		var edges = await jobTrackClient.Query.GetPrerequisitesAsync(new() { Context = context, NodeId = nodeId }, cancellationToken);

		var requiresIds = edges.Where(e => e.DependentJobId == nodeId).Select(e => e.RequiredJobId).ToList();
		var requiredByIds = edges.Where(e => e.RequiredJobId == nodeId).Select(e => e.DependentJobId).ToList();
		var distinctIds = requiresIds.Concat(requiredByIds).Distinct().ToArray();
		if (distinctIds.Length == 0) {
			return;
		}

		var summaries = await jobTrackClient.Query.GetJobSummariesAsync(new() { Context = context, NodeIds = [.. distinctIds] }, cancellationToken);
		var summariesById = summaries.ToDictionary(s => s.Id);

		Requires = [.. requiresIds.Select(id => summariesById.GetValueOrDefault(id)).OfType<JobNodeSummaryResult>()];
		RequiredBy = [.. requiredByIds.Select(id => summariesById.GetValueOrDefault(id)).OfType<JobNodeSummaryResult>()];
	}

	/// <summary>
	///     Resolves the ancestor-declared blockers (see <see cref="AncestorBlockers" />) to display
	///     names: the declaring node is always one of <paramref name="currentNode" />'s ancestors, so it
	///     comes free from the already-fetched breadcrumb; only the blocking jobs themselves need a
	///     summaries fetch. A blocking job that no longer resolves (archived out of the summary set) falls
	///     back to its bare id, matching <see cref="LoadPrerequisitesAndDependentsAsync" />.
	/// </summary>
	private async Task LoadAncestorBlockersAsync(
		CommandContext context, JobNodeDetailResult currentNode, ReadinessResult readiness, CancellationToken cancellationToken)
	{
		var currentNodeId = currentNode.Node.Id;
		var ancestorBlockers = readiness.Blockers.Where(b => b.DeclaredOnJobId != currentNodeId).ToArray();
		if (ancestorBlockers.Length == 0) {
			return;
		}

		var ancestorDescriptionsById = currentNode.Ancestors.ToDictionary(a => a.Id, a => a.Description);

		var requiredIds = ancestorBlockers.Select(b => b.RequiredJobId).Distinct().ToArray();
		var summaries = await jobTrackClient.Query.GetJobSummariesAsync(new() { Context = context, NodeIds = [.. requiredIds] }, cancellationToken);
		var requiredDescriptionsById = summaries.ToDictionary(s => s.Id, s => s.Description);

		AncestorBlockers = [
			.. ancestorBlockers.Select(b => new AncestorBlockerView(
				b.RequiredJobId.Value,
				requiredDescriptionsById.GetValueOrDefault(b.RequiredJobId, $"Job {b.RequiredJobId.Value}"),
				b.DeclaredOnJobId.Value,
				ancestorDescriptionsById.GetValueOrDefault(b.DeclaredOnJobId, $"Job {b.DeclaredOnJobId.Value}"))),
		];
	}

	/// <summary>
	///     Formats an owner for display: display name and username when the id resolves in
	///     <see cref="EmployeeDirectoryById" />, otherwise a fallback that still names the numeric id
	///     (covers an owner disabled or role-revoked since assignment — see
	///     <see cref="EmployeeDirectoryById" />).
	/// </summary>
	public string DescribeOwner(AppUserId? ownerUserId) =>
		EmployeeDirectoryDisplay.Describe(EmployeeDirectoryById, ownerUserId?.Value);

	/// <summary>
	///     One inherited (ancestor-declared) blocker for display: the blocking job and the
	///     ancestor that declared the prerequisite edge, each as an id/description pair the view formats
	///     through <see cref="JobNodeDisplay" />.
	/// </summary>
	public sealed record AncestorBlockerView(
		long RequiredJobId,
		string RequiredDescription,
		long DeclaredOnJobId,
		string DeclaredOnDescription);
}
