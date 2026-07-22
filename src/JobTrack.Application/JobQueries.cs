namespace JobTrack.Application;

using Abstractions;
using Domain.Authorization;
using Domain.Hierarchy;
using NodaTime;
using Ports;

/// <summary>
///     Implements read-only queries (plan §7.3 steps 2 and 5; plan §8.5 slice 2) by loading
///     authoritative data through <see cref="IEmployeeQueryPort" />/<see cref="IReadinessQueryPort" />/
///     <see cref="IJobBrowseQueryPort" />. Employee queries apply <see cref="EmployeeAccessPolicy" />
///     before returning; readiness and job-tree browsing/search have no such gate (see
///     <see cref="GetReadinessRequest" />/<see cref="GetJobNodeRequest" />) — readiness calls the pure
///     <see cref="ReadinessCalculator" /> directly over the port's materialized inputs, while browsing
///     queries pass straight through to <see cref="IJobBrowseQueryPort" />.
/// </summary>
public sealed class JobQueries : IJobQueries
{
	// One employee's rate/schedule history is not offset/limit-paginated like a flat collection --
	// both snapshots are always returned whole so a caller sees a complete, self-consistent picture
	// of effective-dated entries -- so an oversized snapshot is a hard validation failure (400)
	// rather than a silently truncated array (remediation plan §3.1).
	private const int MaxRateEntryCount = 2_000;
	private const int MaxScheduleEntryCount = 2_000;
	private readonly IAwaitingProgressQueryPort _awaitingProgressQueryPort;
	private readonly IJobBrowseQueryPort _browseQueryPort;

	private readonly IClock _clock;
	private readonly ICostQueries _costQueries;
	private readonly IEmployeeQueryPort _employeeQueryPort;
	private readonly ILeafWorkQueryPort _leafWorkQueryPort;
	private readonly IPrerequisiteQueryPort _prerequisiteQueryPort;
	private readonly IRateQueryPort _rateQueryPort;
	private readonly IReadinessQueryPort _readinessQueryPort;
	private readonly IScheduleQueryPort _scheduleQueryPort;
	private readonly IWorkSessionQueryPort _workSessionQueryPort;

	/// <summary>Creates a <see cref="JobQueries" /> over the given ports.</summary>
	public JobQueries(
		IEmployeeQueryPort employeeQueryPort, IReadinessQueryPort readinessQueryPort,
		IJobBrowseQueryPort browseQueryPort, IAwaitingProgressQueryPort awaitingProgressQueryPort,
		IWorkSessionQueryPort workSessionQueryPort,
		ILeafWorkQueryPort leafWorkQueryPort, IPrerequisiteQueryPort prerequisiteQueryPort,
		IScheduleQueryPort scheduleQueryPort, IRateQueryPort rateQueryPort, ICostQueries costQueries, IClock clock)
	{
		ArgumentNullException.ThrowIfNull(employeeQueryPort);
		ArgumentNullException.ThrowIfNull(readinessQueryPort);
		ArgumentNullException.ThrowIfNull(browseQueryPort);
		ArgumentNullException.ThrowIfNull(awaitingProgressQueryPort);
		ArgumentNullException.ThrowIfNull(workSessionQueryPort);
		ArgumentNullException.ThrowIfNull(leafWorkQueryPort);
		ArgumentNullException.ThrowIfNull(prerequisiteQueryPort);
		ArgumentNullException.ThrowIfNull(scheduleQueryPort);
		ArgumentNullException.ThrowIfNull(rateQueryPort);
		ArgumentNullException.ThrowIfNull(costQueries);
		ArgumentNullException.ThrowIfNull(clock);

		_clock = clock;
		_employeeQueryPort = employeeQueryPort;
		_readinessQueryPort = readinessQueryPort;
		_browseQueryPort = browseQueryPort;
		_awaitingProgressQueryPort = awaitingProgressQueryPort;
		_workSessionQueryPort = workSessionQueryPort;
		_leafWorkQueryPort = leafWorkQueryPort;
		_prerequisiteQueryPort = prerequisiteQueryPort;
		_scheduleQueryPort = scheduleQueryPort;
		_rateQueryPort = rateQueryPort;
		_costQueries = costQueries;
	}

	/// <inheritdoc />
	public Task<EmployeeProfileResult> GetEmployeeProfileAsync(
		GetEmployeeProfileRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return GetEmployeeProfileCoreAsync(request, cancellationToken);
	}

	/// <inheritdoc />
	public Task<EquatableArray<EmployeeDirectoryEntry>> GetEmployeeDirectoryAsync(
		GetEmployeeDirectoryRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return GetEmployeeDirectoryCoreAsync(request, cancellationToken);
	}

	/// <inheritdoc />
	public Task<EquatableArray<EmployeeDirectoryEntry>> GetAllEmployeesAsync(
		GetAllEmployeesRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return GetAllEmployeesCoreAsync(request, cancellationToken);
	}

	/// <inheritdoc />
	public Task<AccountStateResult> GetAccountStateAsync(
		GetAccountStateRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return GetAccountStateCoreAsync(request, cancellationToken);
	}

	/// <inheritdoc />
	public Task<ReadinessResult> GetReadinessAsync(GetReadinessRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return GetReadinessCoreAsync(request, cancellationToken);
	}

	/// <inheritdoc />
	public Task<JobNodeDetailResult> GetJobNodeAsync(GetJobNodeRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return GetJobNodeCoreAsync(request, cancellationToken);
	}

	/// <inheritdoc />
	public Task<EquatableArray<JobNodeSummaryResult>> GetJobChildrenAsync(
		GetJobChildrenRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ValidatePaging(request.Offset, request.Limit);

		return GetJobChildrenCoreAsync(request, cancellationToken);
	}

	/// <inheritdoc />
	public Task<EquatableArray<JobNodeSummaryResult>> SearchJobNodesAsync(
		SearchJobNodesRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentException.ThrowIfNullOrWhiteSpace(request.SearchText);
		ValidatePaging(request.Offset, request.Limit);

		return SearchJobNodesCoreAsync(request, cancellationToken);
	}

	/// <inheritdoc />
	public Task<EquatableArray<JobNodeSummaryResult>> GetJobSummariesAsync(
		GetJobSummariesRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return GetJobSummariesCoreAsync(request, cancellationToken);
	}

	/// <inheritdoc />
	public Task<EquatableArray<AwaitingProgressEntry>> GetAwaitingProgressAsync(
		GetAwaitingProgressRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ValidatePaging(request.Offset, request.Limit);
		var limit = request.Limit.HasValue
			? Math.Min(request.Limit.Value, AwaitingProgressPaging.MaxPageSize)
			: AwaitingProgressPaging.DefaultPageSize;

		return GetAwaitingProgressCoreAsync(request, limit, cancellationToken);
	}

	/// <inheritdoc />
	public Task<EquatableArray<WorkSessionResult>> GetLeafSessionsAsync(
		GetLeafSessionsRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ValidatePaging(request.Offset, request.Limit);

		return GetLeafSessionsCoreAsync(request, cancellationToken);
	}

	/// <inheritdoc />
	public Task<EquatableArray<WorkSessionResult>> GetActiveSessionsAsync(
		GetActiveSessionsRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return GetActiveSessionsCoreAsync(request, cancellationToken);
	}

	/// <inheritdoc />
	public Task<EquatableArray<LeafSessionManageCapabilityResult>> GetSessionManageCapabilitiesAsync(
		GetSessionManageCapabilitiesRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return GetSessionManageCapabilitiesCoreAsync(request, cancellationToken);
	}

	/// <inheritdoc />
	public Task<LeafWorkResult> GetLeafWorkAsync(GetLeafWorkRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return GetLeafWorkCoreAsync(request, cancellationToken);
	}

	/// <inheritdoc />
	public Task<LeafWorkPageResult> GetLeafWorkPageAsync(GetLeafWorkPageRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return GetLeafWorkPageCoreAsync(request, cancellationToken);
	}

	/// <inheritdoc />
	public Task<EquatableArray<PrerequisiteEdge>> GetPrerequisitesAsync(
		GetPrerequisitesRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ValidatePaging(request.Offset, request.Limit);

		return GetPrerequisitesCoreAsync(request, cancellationToken);
	}

	/// <inheritdoc />
	public Task<JobSubtreeResult> GetJobSubtreeAsync(GetJobSubtreeRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return GetJobSubtreeCoreAsync(request, cancellationToken);
	}

	/// <inheritdoc />
	public Task<ScheduleSnapshotResult> GetScheduleAsync(GetScheduleRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return GetScheduleCoreAsync(request, cancellationToken);
	}

	/// <inheritdoc />
	public Task<RateSnapshotResult> GetRatesAsync(GetRatesRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return GetRatesCoreAsync(request, cancellationToken);
	}

	/// <summary>
	///     Validates the bounded-collection request shape shared by every paginated query (remediation
	///     plan §3.1) -- a negative offset or a non-positive explicit limit is a caller usage error, not
	///     a valid "return nothing" request.
	/// </summary>
	private static void ValidatePaging(int offset, int? limit)
	{
		if (offset < 0) {
			throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset must be non-negative.");
		}

		if (limit is int value && value <= 0) {
			throw new ArgumentOutOfRangeException(nameof(limit), value, "Limit must be positive when set.");
		}
	}

	private Task<EmployeeProfileResult> GetEmployeeProfileCoreAsync(GetEmployeeProfileRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"query.get-employee-profile", request.Context, JobTrackOperation.WithUserId(request.TargetUserId),
			async () => {
				var actorRoles = await _employeeQueryPort
					.GetActorRolesAsync(request.Context.Actor, cancellationToken)
					.ConfigureAwait(false);

				if (!EmployeeAccessPolicy.CanViewEmployee(request.Context.Actor, request.TargetUserId, actorRoles)) {
					throw new AuthorizationDeniedException(
						$"Actor {request.Context.Actor} may not view employee {request.TargetUserId}'s profile.");
				}

				var result = await _employeeQueryPort
					.GetEmployeeProfileAsync(request.Context.Actor, request.TargetUserId, cancellationToken)
					.ConfigureAwait(false);

				return result.Profile;
			});

	private Task<EquatableArray<EmployeeDirectoryEntry>> GetEmployeeDirectoryCoreAsync(
		GetEmployeeDirectoryRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"query.get-employee-directory", request.Context, null,
			() => _employeeQueryPort.GetEmployeeDirectoryAsync(cancellationToken));

	private Task<EquatableArray<EmployeeDirectoryEntry>> GetAllEmployeesCoreAsync(
		GetAllEmployeesRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"query.get-all-employees", request.Context, null,
			() => _employeeQueryPort.GetAllEmployeesAsync(cancellationToken));

	private Task<AccountStateResult> GetAccountStateCoreAsync(GetAccountStateRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"query.get-account-state", request.Context, JobTrackOperation.WithUserId(request.TargetUserId),
			async () => {
				var actorRoles = await _employeeQueryPort
					.GetActorRolesAsync(request.Context.Actor, cancellationToken)
					.ConfigureAwait(false);

				if (!EmployeeAccessPolicy.CanViewEmployee(request.Context.Actor, request.TargetUserId, actorRoles)) {
					throw new AuthorizationDeniedException(
						$"Actor {request.Context.Actor} may not view employee {request.TargetUserId}'s account state.");
				}

				var result = await _employeeQueryPort
					.GetAccountStateAsync(request.Context.Actor, request.TargetUserId, cancellationToken)
					.ConfigureAwait(false);

				return result.AccountState;
			});

	private Task<ReadinessResult> GetReadinessCoreAsync(GetReadinessRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"query.get-readiness", request.Context, JobTrackOperation.WithNodeId(request.NodeId),
			async () => {
				var inputs = await _readinessQueryPort.GetReadinessInputsAsync(request.NodeId, cancellationToken).ConfigureAwait(false);

				return ReadinessCalculator.IsReady(request.NodeId, inputs.NodesById, inputs.Prerequisites);
			});

	private Task<JobNodeDetailResult> GetJobNodeCoreAsync(GetJobNodeRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"query.get-job-node", request.Context, request.NodeId.HasValue ? JobTrackOperation.WithNodeId(request.NodeId.Value) : null,
			() => _browseQueryPort.GetNodeAsync(request.NodeId, cancellationToken));

	private Task<EquatableArray<JobNodeSummaryResult>> GetJobChildrenCoreAsync(GetJobChildrenRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"query.get-job-children", request.Context, JobTrackOperation.WithNodeId(request.ParentId),
			async () => {
				var children = await _browseQueryPort.GetChildrenAsync(
						request.ParentId, request.Ownership, request.ArchiveFilter, request.Offset, request.Limit, cancellationToken)
					.ConfigureAwait(false);
				return await EnrichSummariesWithCostAsync(request.Context, children, cancellationToken).ConfigureAwait(false);
			});

	private Task<EquatableArray<JobNodeSummaryResult>> SearchJobNodesCoreAsync(SearchJobNodesRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"query.search-job-nodes", request.Context, null,
			async () => {
				var matches = await _browseQueryPort.SearchJobNodesAsync(
						request.SearchText, request.Ownership, request.ArchiveFilter, request.Offset, request.Limit, cancellationToken)
					.ConfigureAwait(false);
				return await EnrichSummariesWithCostAsync(request.Context, matches, cancellationToken).ConfigureAwait(false);
			});

	private Task<EquatableArray<JobNodeSummaryResult>>
		GetJobSummariesCoreAsync(GetJobSummariesRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"query.get-job-summaries", request.Context, null,
			async () => {
				var summaries = await _browseQueryPort.GetSummariesByIdsAsync(request.NodeIds, cancellationToken).ConfigureAwait(false);
				return await EnrichSummariesWithCostAsync(request.Context, summaries, cancellationToken).ConfigureAwait(false);
			});

	private Task<JobSubtreeResult> GetJobSubtreeCoreAsync(GetJobSubtreeRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"query.get-job-subtree", request.Context, JobTrackOperation.WithNodeId(request.RootId),
			async () => {
				var maxDepth = request.MaxDepth ?? JobSubtreeLimits.DefaultMaxDepth;
				var rows = await _browseQueryPort.GetSubtreeAsync(
					request.RootId, maxDepth, request.Ownership, request.ArchiveFilter, cancellationToken).ConfigureAwait(false);

				var spans = JobSubtreeOrdinals.Compute(rows, request.RootId);

				Money? rootTotal = null;
				string? tzdbVersion = null;
				EquatableDictionary<JobNodeId, Money>? displayedCosts = null;
				try {
					var totals = await _costQueries.GetHierarchyTotalsAsync(
						new() { Context = request.Context, NodeId = request.RootId, AsOf = request.AsOf },
						cancellationToken).ConfigureAwait(false);
					rootTotal = totals.DisplayedCosts.GetValueOrDefault(request.RootId);
					tzdbVersion = totals.TzdbVersion;
					displayedCosts = totals.DisplayedCosts;
				}
				catch (AuthorizationDeniedException) {
					// ADR 0039 decision 4 / ADR 0040: cost is an optional field on an otherwise
					// universally browsable subtree, never a whole-request denial.
				}
				catch (ArgumentOutOfRangeException) {
					// The structure fetch is depth/breadth-bounded, while the reused cost hierarchy
					// query deliberately totals the whole subtree and can reject pathological size.
					// Treat that the same as unavailable cost: omit the optional fields, keep Browse usable.
				}

				// ADR 0042: CanView's ownership carve-out admits the whole subtree at once, so each
				// node's *individual* cost is filtered again here — a branch roll-up is an aggregate
				// and stays, but another worker's leaf cost would expose their rate and is dropped.
				var costRoles = displayedCosts is null
					? []
					: await GetCostFilterRolesAsync(request.Context.Actor, cancellationToken).ConfigureAwait(false);

				Money? CostFor(bool hasChildren, AppUserId? ownerUserId, JobNodeId nodeId) =>
					CostAccessPolicy.CanViewNodeCost(costRoles, hasChildren, ownerUserId, request.Context.Actor)
						? displayedCosts?.GetValueOrDefault(nodeId)
						: null;

				// ADR 0043: one materialization of the readiness facts, then the pure calculator per
				// row -- the same inputs a single-node readiness check already loads, so this is one
				// extra round trip whatever the row count, and never one per row.
				var readinessInputs = await _readinessQueryPort
					.GetReadinessInputsAsync(request.RootId, cancellationToken).ConfigureAwait(false);

				var nodes = rows.OrderBy(row => spans[row.Id].Lft).Select(row => new JobSubtreeNodeResult {
					Id = row.Id,
					ParentId = row.ParentId,
					Kind = row.Kind,
					Depth = row.Depth,
					Description = row.Description,
					OwnerUserId = row.OwnerUserId,
					Priority = row.Priority,
					ArchivedAt = row.ArchivedAt,
					HasChildren = row.HasChildren,
					HasLeafWork = row.HasLeafWork,
					Achievement = row.Achievement,
					IsReady = ReadinessCalculator
						.IsReady(row.Id, readinessInputs.NodesById, readinessInputs.Prerequisites).IsReady,
					HasUnexpandedChildren = row.HasUnexpandedChildren,
					MatchesFilter = row.MatchesFilter,
					SubtreeLft = spans[row.Id].Lft,
					SubtreeRgt = spans[row.Id].Rgt,
					Cost = CostFor(row.HasChildren, row.OwnerUserId, row.Id),
				});

				// The root's own total obeys the same rule: browsing a single leaf owned by someone
				// else must not reveal through RootTotal what the node list withholds.
				var rootRow = rows.FirstOrDefault(row => row.Id == request.RootId);
				var displayedRootTotal = rootRow is null
					? rootTotal
					: CostFor(rootRow.HasChildren, rootRow.OwnerUserId, rootRow.Id);

				return new JobSubtreeResult {
					RootId = request.RootId,
					RootTotal = displayedRootTotal,
					TzdbVersion = tzdbVersion,
					Nodes = EquatableArray.CopyOf(nodes),
				};
			});

	private Task<EquatableArray<AwaitingProgressEntry>> GetAwaitingProgressCoreAsync(
		GetAwaitingProgressRequest request, int limit, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"query.get-awaiting-progress", request.Context, null,
			async () => {
				var inputs = await _awaitingProgressQueryPort.GetAwaitingProgressInputsAsync(cancellationToken).ConfigureAwait(false);

				if (request.SubtreeRootId is JobNodeId subtreeRootId && !inputs.NodesById.ContainsKey(subtreeRootId)) {
					throw new EntityNotFoundException($"Job node {subtreeRootId} does not exist.");
				}

				var entries = AwaitingProgressCalculator.GetAwaitingProgress(
					inputs.NodesById, inputs.FactsById, inputs.Prerequisites, request.Ownership, request.SubtreeRootId);

				// Fresh-eyes review §2.8: bound the page before cost enrichment, not after -- the
				// calculator's own ordering is preserved, only the slice offered to the caller changes.
				var page = entries.Skip(request.Offset).Take(limit);

				return await EnrichAwaitingProgressWithCostAsync(request.Context, [.. page], cancellationToken).ConfigureAwait(false);
			});

	private async Task<EquatableArray<JobNodeSummaryResult>> EnrichSummariesWithCostAsync(
		CommandContext context, EquatableArray<JobNodeSummaryResult> summaries, CancellationToken cancellationToken)
	{
		if (summaries.Count == 0) {
			return summaries;
		}

		var asOf = _clock.GetCurrentInstant();
		// ADR 0042: another worker's individual leaf cost stays hidden even where the actor is
		// admitted to the node; a branch's roll-up is an aggregate and remains visible.
		var costRoles = await GetCostFilterRolesAsync(context.Actor, cancellationToken).ConfigureAwait(false);
		var candidateIds = summaries
			.Where(summary => CostAccessPolicy.CanViewNodeCost(costRoles, summary.HasChildren, summary.OwnerUserId, context.Actor))
			.Select(summary => summary.Id)
			.ToArray();

		// Fresh-eyes review §2.8: one bulk snapshot for the whole page, never one round trip per row.
		var displayedCosts = await GetBulkDisplayedCostsAsync(context, candidateIds, asOf, cancellationToken).ConfigureAwait(false);

		return [.. summaries.Select(summary => summary with { Cost = displayedCosts.GetValueOrDefault(summary.Id) })];
	}

	private async Task<EquatableArray<AwaitingProgressEntry>> EnrichAwaitingProgressWithCostAsync(
		CommandContext context, EquatableArray<AwaitingProgressEntry> entries, CancellationToken cancellationToken)
	{
		if (entries.Count == 0) {
			return entries;
		}

		var asOf = _clock.GetCurrentInstant();
		// Awaiting-progress entries are leaves by construction, so the branch-aggregate relief in
		// CanViewNodeCost never applies here: it reduces to "your own or unassigned" (ADR 0042).
		var costRoles = await GetCostFilterRolesAsync(context.Actor, cancellationToken).ConfigureAwait(false);
		var candidateIds = entries
			.Where(entry => CostAccessPolicy.CanViewNodeCost(costRoles, false, entry.OwnerUserId, context.Actor))
			.Select(entry => entry.Id)
			.ToArray();

		var displayedCosts = await GetBulkDisplayedCostsAsync(context, candidateIds, asOf, cancellationToken).ConfigureAwait(false);

		return [.. entries.Select(entry => entry with { Cost = displayedCosts.GetValueOrDefault(entry.Id) })];
	}

	/// <summary>
	///     Prices every candidate in one bulk call (fresh-eyes review §2.8) instead of one
	///     <see cref="ICostQueries.GetHierarchyTotalsAsync" /> round trip per row. Cost is an optional
	///     field on an otherwise universally browsable listing (ADR 0039 decision 4), so a failure here
	///     degrades to "no costs shown" rather than failing the whole listing.
	/// </summary>
	private async Task<EquatableDictionary<JobNodeId, Money>> GetBulkDisplayedCostsAsync(
		CommandContext context, JobNodeId[] candidateIds, Instant asOf, CancellationToken cancellationToken)
	{
		if (candidateIds.Length == 0) {
			return EquatableDictionaryFactory.CopyOf(new Dictionary<JobNodeId, Money>());
		}

		try {
			var result = await _costQueries.GetBulkNodeCostsAsync(
				new() { Context = context, NodeIds = [.. candidateIds], AsOf = asOf }, cancellationToken).ConfigureAwait(false);
			return result.DisplayedCosts;
		}
		catch (AuthorizationDeniedException) {
			return EquatableDictionaryFactory.CopyOf(new Dictionary<JobNodeId, Money>());
		}
		catch (ArgumentOutOfRangeException) {
			return EquatableDictionaryFactory.CopyOf(new Dictionary<JobNodeId, Money>());
		}
		catch (MissingRateException) {
			return EquatableDictionaryFactory.CopyOf(new Dictionary<JobNodeId, Money>());
		}
	}

	/// <summary>
	///     The actor's roles for the per-node cost filter (ADR 0042). Cost is an optional field on an
	///     otherwise universally browsable listing, never a whole-request denial (ADR 0039 decision 4), so
	///     an actor whose roles cannot be resolved yields no roles — the most restrictive answer — rather
	///     than failing the listing outright.
	/// </summary>
	private async Task<EquatableArray<EmployeeRole>> GetCostFilterRolesAsync(AppUserId actor, CancellationToken cancellationToken)
	{
		try {
			return await _employeeQueryPort.GetActorRolesAsync(actor, cancellationToken).ConfigureAwait(false);
		}
		catch (EntityNotFoundException) {
			return [];
		}
	}

	private Task<EquatableArray<WorkSessionResult>> GetLeafSessionsCoreAsync(GetLeafSessionsRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"query.get-leaf-sessions", request.Context, JobTrackOperation.WithNodeId(request.LeafWorkId),
			async () => {
				var result = await _workSessionQueryPort
					.GetSessionsAsync(
						request.Context.Actor, request.LeafWorkId, request.WorkedByUserId, request.Offset, request.Limit, cancellationToken)
					.ConfigureAwait(false);

				if (!WorkSessionAccessPolicy.CanView(result.ActorRoles)) {
					throw new AuthorizationDeniedException(
						$"Actor {request.Context.Actor} may not view sessions on job node {request.LeafWorkId}.");
				}

				return result.Sessions;
			});

	private Task<EquatableArray<WorkSessionResult>>
		GetActiveSessionsCoreAsync(GetActiveSessionsRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"query.get-active-sessions", request.Context, null,
			async () => {
				var result = await _workSessionQueryPort
					.GetActiveSessionsAsync(request.Context.Actor, request.LeafWorkIds, cancellationToken)
					.ConfigureAwait(false);

				if (!WorkSessionAccessPolicy.CanView(result.ActorRoles)) {
					throw new AuthorizationDeniedException($"Actor {request.Context.Actor} may not view active sessions.");
				}

				// ADR 0041 already made viewing recorded work unqualified for any baseline employee
				// role -- the same "recorded work is job data" reasoning applies here, not only to
				// GetLeafSessionsAsync's history read. Narrowing this batch to "sessions I can manage"
				// would silently hide a leaf's other active workers from a plain Worker's Browse/
				// Awaiting-Progress view, defeating the plan §2.4 "never collapse multiple active
				// sessions" guarantee for the common case. CanManage still gates every mutation
				// (start/finish/correct); it plays no role in this read.
				return result.Sessions;
			});

	private Task<EquatableArray<LeafSessionManageCapabilityResult>> GetSessionManageCapabilitiesCoreAsync(
		GetSessionManageCapabilitiesRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"query.get-session-manage-capabilities", request.Context, null,
			async () => {
				if (request.LeafWorkIds.Count == 0) {
					return [];
				}

				var result = await _workSessionQueryPort
					.GetManageCapabilitiesAsync(request.Context.Actor, request.LeafWorkIds, cancellationToken)
					.ConfigureAwait(false);
				var controlled = result.ControlledLeafWorkIds.ToHashSet();
				EquatableArray<LeafSessionManageCapabilityResult> capabilities = [
					.. request.LeafWorkIds.Select(id => new LeafSessionManageCapabilityResult {
						LeafWorkId = id, CanManage = WorkSessionAccessPolicy.CanManage(result.ActorRoles, controlled.Contains(id)),
					}),
				];

				return capabilities;
			});

	private Task<LeafWorkResult> GetLeafWorkCoreAsync(GetLeafWorkRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"query.get-leaf-work", request.Context, JobTrackOperation.WithNodeId(request.JobNodeId),
			() => _leafWorkQueryPort.GetLeafWorkAsync(request.JobNodeId, cancellationToken));

	private Task<EquatableArray<PrerequisiteEdge>> GetPrerequisitesCoreAsync(GetPrerequisitesRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"query.get-prerequisites", request.Context, JobTrackOperation.WithNodeId(request.NodeId),
			() => _prerequisiteQueryPort.GetPrerequisitesAsync(request.NodeId, request.Offset, request.Limit, cancellationToken));

	/// <summary>
	///     Composes already-batched, already-fixed-cost port calls (unified-leaf-workflow plan Stage 4)
	///     into the unified Work page's single call: <see cref="_browseQueryPort" /> for node context,
	///     <see cref="_leafWorkQueryPort" /> for achievement/version, <see cref="_readinessQueryPort" />
	///     for readiness, <see cref="_workSessionQueryPort" /> for active sessions/manage-capabilities
	///     (both already batch-by-leaf, ADR 0044 Stage 4) and a limit-1 lookup for prior participation,
	///     and <see cref="_prerequisiteQueryPort" /> for the dependent count. None of these scale with
	///     session or history count, so neither does this composition.
	/// </summary>
	private Task<LeafWorkPageResult> GetLeafWorkPageCoreAsync(GetLeafWorkPageRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"query.get-leaf-work-page", request.Context, JobTrackOperation.WithNodeId(request.JobNodeId),
			async () => {
				var node = await _browseQueryPort.GetNodeAsync(request.JobNodeId, cancellationToken).ConfigureAwait(false);
				var leafId = request.JobNodeId;

				// Node control (and therefore CanManageSessions/CanComplete's authority test) is a
				// job_node-level fact, independent of whether LeafWork has been attached yet -- a
				// controlling owner can see the "Start for..." disclosure on a brand-new leaf exactly
				// as StartWorkAsync's own authorization already permits them to invoke it.
				var manageCapabilities = await _workSessionQueryPort
					.GetManageCapabilitiesAsync(request.Context.Actor, [leafId], cancellationToken).ConfigureAwait(false);
				var actorControlsNode = manageCapabilities.ControlledLeafWorkIds.Contains(leafId);
				var canManageSessions = WorkSessionAccessPolicy.CanManage(manageCapabilities.ActorRoles, actorControlsNode);
				// ADR 0045 §3.6: the same "controlling owner, Job Manager, or Administrator" test
				// AchievementAccessPolicy.CanSetAchievement already applies to a non-reopening
				// transition governs both CompleteLeafAsync and starting the reopen-and-start
				// composite for a target worker other than the actor.
				var hasElevatedOrControlAuthority = AchievementAccessPolicy.CanSetAchievement(
					manageCapabilities.ActorRoles, actorControlsNode, false);
				var canComplete = hasElevatedOrControlAuthority;

				Achievement? achievement = null;
				long? leafWorkVersion = null;
				EquatableArray<WorkSessionResult> activeSessions = [];
				var actorParticipatedPreviously = false;
				var canReopenAndStartForSelf = false;
				var canReopenAndStartForOthers = false;
				var canReopenWithoutStarting = false;
				var isReady = false;
				var directDependentCount = 0;

				if (node.Node.HasLeafWork) {
					var leafWork = await _leafWorkQueryPort.GetLeafWorkAsync(leafId, cancellationToken).ConfigureAwait(false);
					achievement = leafWork.Achievement;
					leafWorkVersion = leafWork.Version;

					var readinessInputs = await _readinessQueryPort.GetReadinessInputsAsync(leafId, cancellationToken).ConfigureAwait(false);
					isReady = ReadinessCalculator.IsReady(leafId, readinessInputs.NodesById, readinessInputs.Prerequisites).IsReady;

					var activeSessionsResult = await _workSessionQueryPort
						.GetActiveSessionsAsync(request.Context.Actor, [leafId], cancellationToken).ConfigureAwait(false);
					activeSessions = activeSessionsResult.Sessions;

					var priorParticipation = await _workSessionQueryPort.GetSessionsAsync(
							request.Context.Actor, leafId, request.Context.Actor, 0, 1, cancellationToken)
						.ConfigureAwait(false);
					actorParticipatedPreviously = priorParticipation.Sessions.Count > 0;

					var isTerminal = AchievementTransitions.IsCompletedState(achievement.Value);
					canReopenAndStartForSelf = isTerminal && LeafReopenAndStartAccessPolicy.CanReopenAndStartFor(
						manageCapabilities.ActorRoles, actorControlsNode, actorParticipatedPreviously,
						request.Context.Actor, request.Context.Actor);
					canReopenAndStartForOthers = isTerminal && hasElevatedOrControlAuthority;
					canReopenWithoutStarting = isTerminal && AchievementAccessPolicy.CanSetAchievement(
						manageCapabilities.ActorRoles, actorControlsNode, true);

					directDependentCount = await _prerequisiteQueryPort
						.CountDirectDependentsAsync(leafId, cancellationToken).ConfigureAwait(false);
				}

				return new LeafWorkPageResult {
					JobNodeId = request.JobNodeId,
					Description = node.Node.Description,
					OwnerUserId = node.Node.OwnerUserId,
					ArchivedAt = node.Node.ArchivedAt,
					HasLeafWork = node.Node.HasLeafWork,
					Achievement = achievement,
					LeafWorkVersion = leafWorkVersion,
					IsReady = isReady,
					ActiveSessions = activeSessions,
					ActorControlsNode = actorControlsNode,
					ActorParticipatedPreviously = actorParticipatedPreviously,
					CanManageSessions = canManageSessions,
					CanComplete = canComplete,
					CanReopenAndStartForSelf = canReopenAndStartForSelf,
					CanReopenAndStartForOthers = canReopenAndStartForOthers,
					CanReopenWithoutStarting = canReopenWithoutStarting,
					DirectDependentCount = directDependentCount,
				};
			});

	private Task<ScheduleSnapshotResult> GetScheduleCoreAsync(GetScheduleRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"query.get-schedule", request.Context, JobTrackOperation.WithUserId(request.UserId),
			async () => {
				var result = await _scheduleQueryPort
					.GetScheduleAsync(request.Context.Actor, request.UserId, cancellationToken)
					.ConfigureAwait(false);

				if (!ScheduleAccessPolicy.CanManage(result.ActorRoles, request.Context.Actor == request.UserId)) {
					throw new AuthorizationDeniedException(
						$"Actor {request.Context.Actor} may not view {request.UserId}'s schedule.");
				}

				var entryCount = result.Versions.Count + result.Exceptions.Count;
				if (entryCount > MaxScheduleEntryCount) {
					throw new ArgumentOutOfRangeException(
						nameof(request),
						entryCount,
						$"This employee's schedule has {entryCount} entries, exceeding the {MaxScheduleEntryCount}-entry maximum.");
				}

				return new ScheduleSnapshotResult { Versions = result.Versions, Exceptions = result.Exceptions };
			});

	private Task<RateSnapshotResult> GetRatesCoreAsync(GetRatesRequest request, CancellationToken cancellationToken) =>
		JobTrackOperation.TraceAsync(
			"query.get-rates", request.Context, JobTrackOperation.WithUserId(request.UserId),
			async () => {
				var result = await _rateQueryPort
					.GetRatesAsync(request.Context.Actor, request.UserId, cancellationToken)
					.ConfigureAwait(false);

				if (!CostAccessPolicy.CanView(result.ActorRoles, false)) {
					throw new AuthorizationDeniedException(
						$"Actor {request.Context.Actor} may not view {request.UserId}'s rates.");
				}

				var entryCount = result.UserCostRates.Count + result.NodeRateOverrides.Count;
				if (entryCount > MaxRateEntryCount) {
					throw new ArgumentOutOfRangeException(
						nameof(request),
						entryCount,
						$"This employee's rates have {entryCount} entries, exceeding the {MaxRateEntryCount}-entry maximum.");
				}

				return new RateSnapshotResult { UserCostRates = result.UserCostRates, NodeRateOverrides = result.NodeRateOverrides };
			});
}
