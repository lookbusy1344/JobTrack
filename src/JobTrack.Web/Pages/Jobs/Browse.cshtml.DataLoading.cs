namespace JobTrack.Web.Pages.Jobs;

using System.Globalization;
using Abstractions;
using Application;
using Domain.Costing;
using Domain.Hierarchy;
using Microsoft.AspNetCore.Mvc.Rendering;

public sealed partial class BrowseModel
{
	private async Task LoadAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		CurrentActorId = actor;
		ViewerZone = await viewerTimeZoneResolver.ResolveAsync(actor, cancellationToken);
		var context = new CommandContext {
			Actor = actor,
			CorrelationId = Guid.NewGuid(),
		};

		// Owner filter is remembered across visits (the whole tree is browsable, so the default when
		// nothing is remembered is "All owners"). UnassignedOnly still overrides it below when set.
		OwnerUserId = FilterMemory.Resolve(
			FilterMemoryStore, OwnerFilterSessionKey, Request.Query.ContainsKey(nameof(OwnerUserId)), OwnerUserId, null);

		ShowArchived = ResolveShowArchived();

		var ownerFilter = (UnassignedOnly, OwnerUserId) switch {
			(true, _) => OwnershipFilter.Unassigned,
			(false, long ownerUserId) => OwnershipFilter.OwnedBy(new(ownerUserId)),
			(false, null) => OwnershipFilter.All,
		};

		await LoadEmployeeDirectoryAsync(context, cancellationToken);
		await LoadHomeNodeAsync(context, actor, cancellationToken);

		// A bare visit that named no node at all -- the header's "Jobs" link -- roots at the actor's own
		// home node rather than the tree root, matching AwaitingProgressModel's own home-node default.
		// The query string, not the bound value, is what decides: an explicit nodeId (the root's own
		// included, which the breadcrumb's root link carries) always wins.
		if (!Request.Query.ContainsKey(nameof(NodeId))) {
			NodeId = HomeNodeId?.Value;
		}

		if (IsSearch || ShowSearchEntry) {
			SearchOriginNodeId = ResolveSearchOrigin();
		} else {
			// Not a search view -- this is the node (or root, stored as an empty string) the viewer is
			// actually browsing, so it becomes the "Browse" button's target next time Search opens.
			FilterMemoryStore.SetString(LastBrowsedNodeSessionKey, NodeId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
		}

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

		if (ShowSearchEntry) {
			return;
		}

		try {
			CurrentNode = await jobTrackClient.Query.GetJobNodeAsync(
				new() {
					Context = context,
					NodeId = NodeId.HasValue ? new JobNodeId(NodeId.Value) : null,
				}, cancellationToken);

			// The subtree/children listing is no longer owner-filterable from Browse itself (the owner
			// filter moved into the dedicated Search flow above, since it was easily mistaken for
			// scoping the currently browsed subtree when it in fact scoped a whole-tree search) --
			// always the unfiltered-by-owner subtree here. ShowArchived stays: it is the one toggle
			// that does genuinely scope this subtree, not a whole-tree search.
			Subtree = await jobTrackClient.Query.GetJobSubtreeAsync(new() {
				Context = context,
				RootId = CurrentNode.Node.Id,
				Ownership = OwnershipFilter.All,
				ArchiveFilter = ShowArchived ? JobArchiveFilter.All : JobArchiveFilter.ActiveOnly,
				AsOf = Now,
			}, cancellationToken);
			CurrentNodeBranchAchievement = Subtree.RootAchievement;

			Readiness = await jobTrackClient.Query.GetReadinessAsync(new() {
				Context = context,
				NodeId = CurrentNode.Node.Id,
			}, cancellationToken);

			await LoadPrerequisitesAndDependentsAsync(context, CurrentNode.Node.Id, cancellationToken);
			await LoadAncestorBlockersAsync(context, CurrentNode, Readiness, cancellationToken);
			await LoadActiveSessionsAsync(context, cancellationToken);
			await LoadRequestContextAsync(context, CurrentNode.Node.Id, cancellationToken);

			if (CurrentNode.Node.Kind == NodeKind.Leaf) {
				await LoadCurrentNodeAchievementAsync(context, CurrentNode.Node, cancellationToken);
				await LoadLeafSessionsPanelAsync(context, CurrentNode.Node.Id, cancellationToken);
			}

			await LoadRecentNodesAsync(context, CurrentNode.Node.Id, cancellationToken);
		}
		catch (EntityNotFoundException) {
			if (NodeId is long missingNodeId) {
				ForgetRecentNode(new(missingNodeId));
			}
			ErrorMessage = "That job node does not exist.";
		}
	}

	private async Task LoadRecentNodesAsync(CommandContext context, JobNodeId currentNodeId, CancellationToken cancellationToken)
	{
		if (RecentHistoryWasCleared) {
			return;
		}

		var rememberedIds = ReadRecentNodeIds();
		var visibleIds = rememberedIds.Where(id => id != currentNodeId).ToArray();
		if (visibleIds.Length > 0) {
			var summaries = await jobTrackClient.Query.GetJobSummariesAsync(new() {
				Context = context,
				NodeIds = [.. visibleIds],
			}, cancellationToken);
			var summariesById = summaries.ToDictionary(summary => summary.Id);
			RecentNodes = [.. visibleIds.Where(summariesById.ContainsKey).Select(id => summariesById[id])];
		}

		var updatedIds = new[] {
							 currentNodeId,
						 }
						 .Concat(rememberedIds.Where(id => id != currentNodeId))
						 .Take(RecentNodeCap);
		WriteRecentNodeIds(updatedIds);
	}

	private void ForgetRecentNode(JobNodeId nodeId) =>
		WriteRecentNodeIds(ReadRecentNodeIds().Where(id => id != nodeId));

	private JobNodeId[] ReadRecentNodeIds()
	{
		var stored = FilterMemoryStore.GetString(RecentNodeIdsSessionKey);
		if (string.IsNullOrEmpty(stored)) {
			return [];
		}

		return stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
					 .Select(value => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0)
					 .Where(value => value > 0)
					 .Select(value => new JobNodeId(value))
					 .Distinct()
					 .Take(RecentNodeCap)
					 .ToArray();
	}

	private void WriteRecentNodeIds(IEnumerable<JobNodeId> nodeIds) =>
		FilterMemoryStore.SetString(
			RecentNodeIdsSessionKey,
			string.Join(',', nodeIds.Select(id => id.Value.ToString(CultureInfo.InvariantCulture))));

	private async Task LoadRequestContextAsync(CommandContext context, JobNodeId nodeId, CancellationToken cancellationToken)
	{
		try {
			RequestContext = await jobTrackClient.Requests.GetDetailAsync(new() {
				Context = context,
				NodeId = nodeId,
			}, cancellationToken);
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
			var sessions = await jobTrackClient.Query.GetActiveSessionsAsync(new() {
				Context = context,
				LeafWorkIds = [.. leafIds],
			},
				cancellationToken);

			ActiveSessionsByLeaf = ActiveSessionGrouping.Group(sessions);
		}
		catch (AuthorizationDeniedException) {
			ActiveSessionsByLeaf = new Dictionary<JobNodeId, EquatableArray<WorkSessionResult>>();
		}

		// Batched rendering hint for the "Start for..." disclosure and another worker's exact finish
		// (ADR 0044 Stage 4/6) -- one round trip regardless of leaf count, never re-derived per row.
		var capabilities = await jobTrackClient.Query.GetSessionManageCapabilitiesAsync(
			new() {
				Context = context,
				LeafWorkIds = [.. leafIds],
			}, cancellationToken);
		CanManageByLeaf = capabilities.ToDictionary(c => c.LeafWorkId, c => c.CanManage);
	}

	/// <summary>
	///     Resolves the effective "Show archived" toggle and keeps <see cref="ShowArchivedSessionKey" />
	///     current in one step, mirroring <see cref="FilterMemory.Resolve" />'s own explicit-vs-remembered
	///     precedence: when the query string carried <see cref="ShowArchived" /> (the toggle link always
	///     sends it explicitly), that choice is used and remembered; otherwise the last remembered choice
	///     applies, defaulting to <see langword="false" /> (active-only) when nothing has been remembered
	///     yet.
	/// </summary>
	private bool ResolveShowArchived()
	{
		if (Request.Query.ContainsKey(nameof(ShowArchived))) {
			FilterMemoryStore.SetString(ShowArchivedSessionKey, ShowArchived.ToString(CultureInfo.InvariantCulture));
			return ShowArchived;
		}

		var remembered = FilterMemoryStore.GetString(ShowArchivedSessionKey);
		return remembered is not null && bool.Parse(remembered);
	}

	/// <summary>
	///     Resolves <see cref="SearchOriginNodeId" />: the last node remembered under
	///     <see cref="LastBrowsedNodeSessionKey" /> (an empty stored string means the root, matching
	///     <see cref="FilterMemory" />'s own convention), falling back to <see cref="HomeNodeId" /> when
	///     nothing has been remembered yet this session -- <see langword="null" /> in the end means the
	///     root, the last-resort fallback.
	/// </summary>
	private JobNodeId? ResolveSearchOrigin()
	{
		var remembered = FilterMemoryStore.GetString(LastBrowsedNodeSessionKey);
		if (remembered is null) {
			return HomeNodeId;
		}

		return remembered.Length == 0 ? null : new JobNodeId(long.Parse(remembered, CultureInfo.InvariantCulture));
	}

	private async Task LoadEmployeeDirectoryAsync(CommandContext context, CancellationToken cancellationToken)
	{
		_employeeDirectory = await jobTrackClient.Query.GetEmployeeDirectoryAsync(
			new() {
				Context = context,
			}, cancellationToken);

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
			new() {
				Context = context,
				JobNodeId = node.Id,
			}, cancellationToken);
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
				new() {
					Context = context,
					LeafWorkId = leafId,
					WorkedByUserId = null,
				}, cancellationToken);

			Panel = new() {
				LeafNodeId = leafId.Value,
				ViewerZone = ViewerZone,
				Now = Now,
				DisplayedWorkedByUserId = null,
				DisplayedWorkedByName = null,
				Sessions = sessions,
				EmployeeDirectoryById = EmployeeDirectoryById,
				WorkedByOptions = [],
				ShowWorkerFilter = false,
				ExtraHiddenFields = RowStateFields,
				SessionCosts = await LoadSessionCostsAsync(context, leafId, cancellationToken),
			};
		}
		catch (AuthorizationDeniedException) {
			ErrorMessage = "You may not view that worker's sessions on this leaf.";
		}
	}

	/// <summary>
	///     Each session's cost on <paramref name="leafId" />, or <see langword="null" /> when cost is
	///     unavailable — the same optional-field treatment as the leaf's own Cost record-card field
	///     (ADR 0039 decision 4/ADR 0040/ADR 0042): an unauthorized viewer or a session with no
	///     resolvable rate withdraws the whole column rather than denying the page or a single row.
	/// </summary>
	private async Task<IReadOnlyDictionary<WorkSessionId, (Money Cost, AllocatedDuration Duration)>?> LoadSessionCostsAsync(
		CommandContext context, JobNodeId leafId, CancellationToken cancellationToken)
	{
		try {
			var details = await jobTrackClient.Costs.GetCostDetailsAsync(
				new() {
					Context = context,
					NodeId = leafId,
					AsOf = Now,
				}, cancellationToken);
			return SessionCostAggregator.AggregateBySession(details.Trace);
		}
		catch (AuthorizationDeniedException) {
			return null;
		}
		catch (MissingRateException) {
			return null;
		}
	}

	private async Task LoadHomeNodeAsync(CommandContext context, AppUserId actor, CancellationToken cancellationToken)
	{
		var profile = await jobTrackClient.Query.GetEmployeeProfileAsync(
			new() {
				Context = context,
				TargetUserId = actor,
			}, cancellationToken);

		HomeNodeId = profile.HomeNodeId;
	}

	private async Task<AppUserId?> ResolveActorAsync()
	{
		var actor = await userManager.GetUserAsync(User);
		return actor?.AppUserId;
	}

	private async Task LoadPrerequisitesAndDependentsAsync(CommandContext context, JobNodeId nodeId, CancellationToken cancellationToken)
	{
		var edges = await jobTrackClient.Query.GetPrerequisitesAsync(new() {
			Context = context,
			NodeId = nodeId,
		}, cancellationToken);

		var requiresIds = edges.Where(e => e.DependentJobId == nodeId).Select(e => e.RequiredJobId).ToList();
		var requiredByIds = edges.Where(e => e.RequiredJobId == nodeId).Select(e => e.DependentJobId).ToList();
		var distinctIds = requiresIds.Concat(requiredByIds).Distinct().ToArray();
		if (distinctIds.Length == 0) {
			return;
		}

		var summaries = await jobTrackClient.Query.GetJobSummariesAsync(new() {
			Context = context,
			NodeIds = [.. distinctIds],
		}, cancellationToken);
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
		var summaries = await jobTrackClient.Query.GetJobSummariesAsync(new() {
			Context = context,
			NodeIds = [.. requiredIds],
		}, cancellationToken);
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
