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
public sealed class BrowseModel(IJobTrackClient jobTrackClient, UserManager<JobTrackIdentityUser> userManager) : PageModel
{
	private EquatableArray<EmployeeDirectoryEntry> _employeeDirectory = [];

	[BindProperty(SupportsGet = true)] public long? NodeId { get; init; }

	[BindProperty(SupportsGet = true)] public long? OwnerUserId { get; init; }

	/// <summary>
	///     When set, overrides <see cref="OwnerUserId" /> to show only the unassigned pool
	///     (ownership model §2.1) — the two are mutually exclusive filter shapes
	///     <see cref="OwnershipFilter" /> exists to keep distinct.
	/// </summary>
	[BindProperty(SupportsGet = true)]
	public bool UnassignedOnly { get; init; }

	[BindProperty(SupportsGet = true)] public JobArchiveFilter ArchiveFilter { get; init; } = JobArchiveFilter.ActiveOnly;

	[BindProperty(SupportsGet = true)] public string? SearchText { get; init; }

	/// <summary>
	///     When viewing a leaf, whose sessions to show — defaults to the actor. Distinct from
	///     <see cref="OwnerUserId" /> (a children-list ownership filter).
	/// </summary>
	[BindProperty(SupportsGet = true)]
	public long? WorkedByUserId { get; init; }

	public string? ErrorMessage { get; private set; }

	public string? SuccessMessage { get; private set; }

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

	public IReadOnlyDictionary<JobNodeId, WorkSessionResult> ActiveSessionByLeaf { get; private set; } =
		new Dictionary<JobNodeId, WorkSessionResult>();

	/// <summary>
	///     When <see cref="CurrentNode" /> is a leaf, its work-session panel: session history,
	///     worked-by picker, and start/finish actions — the same content <c>Work</c> shows, folded in so
	///     viewing a leaf's own detail never requires navigating to a second page for anything
	///     time-tracking related (<c>Work</c> itself remains a stable deep link).
	/// </summary>
	public LeafWorkSessionsPanelModel? LeafWorkSessions { get; private set; }

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

	/// <summary>
	///     The current actor's configured home node (see <see cref="EmployeeProfileResult.HomeNodeId" />),
	///     for showing/hiding the "Set as home node"/"Reset to root" toolbar actions below.
	/// </summary>
	public JobNodeId? HomeNodeId { get; private set; }

	public bool IsSearch => !string.IsNullOrWhiteSpace(SearchText);

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

	public async Task<IActionResult> OnPostStartAsync(long leafNodeId, DateTimeOffset? startedAt, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		try {
			_ = await jobTrackClient.Work.StartWorkAsync(new() {
				Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
				JobNodeId = new(leafNodeId),
				WorkedByUserId = actor.Value,
				StartedAt = startedAt is { } value ? Instant.FromDateTimeOffset(value) : null,
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
			ErrorMessage = DescribeStartFinishFailure(ex);
		}
		catch (PrerequisiteBlockedException) {
			ErrorMessage = "This leaf's prerequisites are not satisfied.";
		}

		await LoadAsync(actor.Value, cancellationToken);
		return Page();
	}

	public async Task<IActionResult> OnPostFinishAsync(
		long sessionId, long version, DateTimeOffset? finishedAt, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		try {
			_ = await jobTrackClient.Work.FinishSessionAsync(new() {
				Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() },
				SessionId = new(sessionId),
				Version = version,
				FinishedAt = finishedAt is { } value ? Instant.FromDateTimeOffset(value) : null,
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
			ErrorMessage = DescribeStartFinishFailure(ex);
		}

		await LoadAsync(actor.Value, cancellationToken);
		return Page();
	}

	public async Task<IActionResult> OnPostPickUpAsync(long nodeId, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		try {
			_ = await jobTrackClient.Jobs.PickUpAsync(
				new() { Context = new() { Actor = actor.Value, CorrelationId = Guid.NewGuid() }, NodeId = new(nodeId) }, cancellationToken);
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

		await LoadAsync(actor.Value, cancellationToken);
		return Page();
	}

	public async Task<IActionResult> OnPostSetHomeNodeAsync(long nodeId, CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await SetHomeNodeAsync(actor.Value, new JobNodeId(nodeId), cancellationToken);
		return Page();
	}

	public async Task<IActionResult> OnPostResetHomeNodeAsync(CancellationToken cancellationToken)
	{
		var actor = await ResolveActorAsync();
		if (actor is null) {
			return Challenge();
		}

		await SetHomeNodeAsync(actor.Value, null, cancellationToken);
		return Page();
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

		await LoadAsync(actor, cancellationToken);
	}

	private static string DescribeStartFinishFailure(InvariantViolationException ex) =>
		ex.ConstraintId switch {
			"work-session-already-active" => "This worker already has an active session for this leaf.",
			"work-session-start-in-future" or "work-session-finish-in-future" => "That time is in the future — enter a past time.",
			"work-session-overlap" => "That time overlaps another session for this leaf.",
			"work-session-invalid-interval" => "The finish time must be after the start time.",
			_ => ex.Message,
		};

	private async Task LoadAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		var context = new CommandContext { Actor = actor, CorrelationId = Guid.NewGuid() };
		var ownerFilter = (UnassignedOnly, OwnerUserId) switch {
			(true, _) => OwnershipFilter.Unassigned,
			(false, { } ownerUserId) => OwnershipFilter.OwnedBy(new(ownerUserId)),
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
				new() { Context = context, NodeId = NodeId is { } nodeId ? new JobNodeId(nodeId) : null }, cancellationToken);

			Subtree = await jobTrackClient.Query.GetJobSubtreeAsync(new() {
				Context = context,
				RootId = CurrentNode.Node.Id,
				Ownership = ownerFilter,
				ArchiveFilter = ArchiveFilter,
				AsOf = SystemClock.Instance.GetCurrentInstant(),
			}, cancellationToken);

			Readiness = await jobTrackClient.Query.GetReadinessAsync(new() { Context = context, NodeId = CurrentNode.Node.Id }, cancellationToken);

			await LoadPrerequisitesAndDependentsAsync(context, CurrentNode.Node.Id, cancellationToken);
			await LoadAncestorBlockersAsync(context, CurrentNode, Readiness, cancellationToken);
			await LoadActiveSessionsAsync(context, cancellationToken);
			await LoadRequestContextAsync(context, CurrentNode.Node.Id, cancellationToken);

			if (CurrentNode.Node.Kind == NodeKind.Leaf) {
				await LoadLeafWorkSessionsAsync(context, actor, CurrentNode.Node.Id, cancellationToken);
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
			ActiveSessionByLeaf = new Dictionary<JobNodeId, WorkSessionResult>();
			return;
		}

		try {
			var sessions = await jobTrackClient.Query.GetActiveSessionsAsync(new() { Context = context, LeafWorkIds = [.. leafIds] },
				cancellationToken);

			ActiveSessionByLeaf = sessions.ToDictionary(s => s.LeafWorkId);
		}
		catch (AuthorizationDeniedException) {
			ActiveSessionByLeaf = new Dictionary<JobNodeId, WorkSessionResult>();
		}
	}

	private async Task LoadEmployeeDirectoryAsync(CommandContext context, CancellationToken cancellationToken)
	{
		_employeeDirectory = await jobTrackClient.Query.GetEmployeeDirectoryAsync(
			new() { Context = context }, cancellationToken);

		EmployeeDirectoryById = _employeeDirectory.ToDictionary(entry => entry.Id);
		OwnerFilterOptions = EmployeeDirectoryDisplay.BuildOptions(_employeeDirectory, new SelectListItem("All owners", string.Empty));
	}

	private async Task LoadLeafWorkSessionsAsync(CommandContext context, AppUserId actor, JobNodeId leafNodeId, CancellationToken cancellationToken)
	{
		// Unset means every worker's sessions on this leaf, not the actor's own (ADR 0041): the whole
		// record of work is what a reader wants first, and it is job data every employee may read.
		var workedByUserId = WorkedByUserId is { } id ? new AppUserId(id) : (AppUserId?)null;

		try {
			var sessions = await jobTrackClient.Query.GetLeafSessionsAsync(
				new() { Context = context, LeafWorkId = leafNodeId, WorkedByUserId = workedByUserId }, cancellationToken);

			LeafWorkSessions = new() {
				LeafNodeId = leafNodeId.Value,
				DisplayedWorkedByUserId = workedByUserId?.Value,
				DisplayedWorkedByName = workedByUserId is { } filtered
					? EmployeeDirectoryDisplay.Describe(EmployeeDirectoryById, filtered.Value, "Unknown")
					: null,
				Sessions = sessions,
				EmployeeDirectoryById = EmployeeDirectoryById,
				WorkedByOptions = BuildWorkerFilterOptions(workedByUserId),
				ExtraHiddenFields = new Dictionary<string, string?> {
					["NodeId"] = NodeId?.ToString(CultureInfo.InvariantCulture),
					["OwnerUserId"] = OwnerUserId?.ToString(CultureInfo.InvariantCulture),
					["ArchiveFilter"] = ArchiveFilter.ToString(),
					["SearchText"] = SearchText,
				},
			};
		}
		catch (AuthorizationDeniedException) {
			ErrorMessage = "You may not view that worker's sessions on this leaf.";
		}
	}

	/// <summary>
	///     Worker-filter options for the sessions panel: an "Everyone" default (empty value,
	///     clearing the filter back to every worker's sessions) followed by each employee, with the
	///     currently filtered worker preselected.
	/// </summary>
	private List<SelectListItem> BuildWorkerFilterOptions(AppUserId? selectedId)
	{
		var options = EmployeeDirectoryDisplay.BuildOptions(_employeeDirectory, new SelectListItem("Everyone", string.Empty));
		if (selectedId is { } id) {
			foreach (var option in options) {
				option.Selected = option.Value == id.Value.ToString(CultureInfo.InvariantCulture);
			}
		}

		return options;
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
