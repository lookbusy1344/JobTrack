namespace JobTrack.Application;

using Abstractions;
using Domain.Hierarchy;

/// <summary>
///     Read-only queries (plan §7.3 steps 2 and 5; plan §8.5 slice 2; docs/api/jobtrack-client-design.md).
///     Employee profile and account-state queries land first (step 2); prerequisite-readiness queries
///     (step 5) follow; job-tree browsing, search, ownership, and archive-filter queries (plan §8.5
///     slice 2) follow those. Achievement queries remain for a later slice.
/// </summary>
public interface IJobQueries
{
	/// <summary>
	///     Retrieves an employee's profile.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor is neither the target employee nor an <see cref="EmployeeRole.Administrator" />.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The target employee does not exist.</exception>
	Task<EmployeeProfileResult> GetEmployeeProfileAsync(
		GetEmployeeProfileRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves every enabled workflow employee's directory-visible identity (display name and
	///     login username) — see <see cref="EmployeeDirectoryEntry" />. Filtered to
	///     <see cref="EmployeeRole.Administrator" />, <see cref="EmployeeRole.JobManager" />, and
	///     <see cref="EmployeeRole.Worker" />, the same roles eligible to own a job node, and excludes
	///     disabled accounts and every account holding <see cref="EmployeeRole.Requester" /> even when
	///     combined with a workflow role — mirrors the existing web-layer workflow-employee dropdown filter.
	///     Gated by <see cref="Domain.Authorization.JobDataAccessPolicy.CanBrowseJobData" /> (remediation
	///     plan §2.4): the actor must hold at least one of the six baseline operational roles.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor holds no baseline operational role permitted to browse job data.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The actor does not exist.</exception>
	Task<EquatableArray<EmployeeDirectoryEntry>> GetEmployeeDirectoryAsync(
		GetEmployeeDirectoryRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves every employee's directory-visible identity (display name and login username) —
	///     see <see cref="EmployeeDirectoryEntry" /> — across every <see cref="EmployeeRole" /> and
	///     enabled state, unlike <see cref="GetEmployeeDirectoryAsync" />'s workflow-only,
	///     enabled-only scope. For admin lookups that target any employee (rota, rates, role
	///     assignment, account management), where a disabled or non-workflow account must still be
	///     findable by name. Requires <see cref="EmployeeRole.Administrator" /> (remediation plan
	///     §2.4) — unlike <see cref="GetEmployeeDirectoryAsync" />, this exposes disabled and
	///     non-workflow accounts, so it is narrower than the general baseline-employee admission.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">The actor does not hold <see cref="EmployeeRole.Administrator" />.</exception>
	/// <exception cref="EntityNotFoundException">The actor does not exist.</exception>
	Task<EquatableArray<EmployeeDirectoryEntry>> GetAllEmployeesAsync(
		GetAllEmployeesRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves an employee's account state and role assignments.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor is neither the target employee nor an <see cref="EmployeeRole.Administrator" />.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The target employee does not exist.</exception>
	Task<AccountStateResult> GetAccountStateAsync(
		GetAccountStateRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Evaluates prerequisite readiness for a node (spec §6): whether every prerequisite declared
	///     directly on it or on any of its ancestors is satisfied. Carries no ownership-based
	///     authorization gate (see <see cref="GetReadinessRequest" />) beyond the baseline-employee
	///     admission every general job/work read shares (remediation plan §2.4; see
	///     <see cref="Domain.Authorization.JobDataAccessPolicy.CanBrowseJobData" />), unlike
	///     employee-account data.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor holds no baseline operational role permitted to browse job data.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The actor or the node does not exist.</exception>
	Task<ReadinessResult> GetReadinessAsync(GetReadinessRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves a node's full detail and root-first ancestor breadcrumb. Carries no
	///     ownership-based authorization gate (see <see cref="GetReadinessRequest" />) beyond the
	///     baseline-employee admission every general job/work read shares (remediation plan §2.4).
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor holds no baseline operational role permitted to browse job data.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The actor or the node does not exist.</exception>
	Task<JobNodeDetailResult> GetJobNodeAsync(GetJobNodeRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves a node's direct children, filtered by owner and archive scope. Carries no
	///     ownership-based authorization gate (see <see cref="GetJobNodeAsync" />) beyond the
	///     baseline-employee admission every general job/work read shares (remediation plan §2.4).
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor holds no baseline operational role permitted to browse job data.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The actor or the parent node does not exist.</exception>
	Task<EquatableArray<JobNodeSummaryResult>> GetJobChildrenAsync(
		GetJobChildrenRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Searches every node's description for a case-insensitive substring match, filtered by owner
	///     and archive scope. Carries no ownership-based authorization gate (see
	///     <see cref="GetJobNodeAsync" />) beyond the baseline-employee admission every general
	///     job/work read shares (remediation plan §2.4).
	/// </summary>
	/// <exception cref="ArgumentException"><see cref="SearchJobNodesRequest.SearchText" /> is blank.</exception>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor holds no baseline operational role permitted to browse job data.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The actor does not exist.</exception>
	Task<EquatableArray<JobNodeSummaryResult>> SearchJobNodesAsync(
		SearchJobNodesRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Describes whatever subset of <see cref="GetJobSummariesRequest.NodeIds" /> currently resolves
	///     to a node, archived or not. Carries no ownership-based authorization gate (see
	///     <see cref="GetJobNodeAsync" />) beyond the baseline-employee admission every general
	///     job/work read shares (remediation plan §2.4) and, unlike a single-node lookup, never throws
	///     for an id that no longer resolves — see <see cref="GetJobSummariesRequest" />.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor holds no baseline operational role permitted to browse job data.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The actor does not exist.</exception>
	Task<EquatableArray<JobNodeSummaryResult>> GetJobSummariesAsync(
		GetJobSummariesRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves the flat "jobs awaiting progress" list: leaves only — never a branch or the root —
	///     that are <see cref="Achievement.Waiting" /> or <see cref="Achievement.InProgress" /> (or have
	///     no leaf work attached yet) and are not archived, optionally scoped to one owner and/or one
	///     subtree, ordered by readiness first — every leaf blocked by an unsatisfied prerequisite (per
	///     <see cref="GetReadinessAsync" />'s <see cref="ReadinessCalculator" />) sorts below every ready
	///     one, since nothing can be done about it — then by descending priority and ascending deadline.
	///     A blocked leaf still appears, carrying <see cref="AwaitingProgressEntry.IsReady" />
	///     <see langword="false" />, unless <see cref="GetAwaitingProgressRequest.ExcludeBlocked" /> is
	///     set. Carries no ownership-based authorization gate (see <see cref="GetJobNodeAsync" />)
	///     beyond the baseline-employee admission every general job/work read shares (remediation
	///     plan §2.4).
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor holds no baseline operational role permitted to browse job data.
	/// </exception>
	/// <exception cref="EntityNotFoundException">
	///     The actor does not exist, or <see cref="GetAwaitingProgressRequest.SubtreeRootId" /> is set
	///     and does not exist.
	/// </exception>
	Task<EquatableArray<AwaitingProgressEntry>> GetAwaitingProgressAsync(
		GetAwaitingProgressRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves which other jobs the node's own workers were clocked on to at the same time as its
	///     sessions, and for how long — one row per (worker, other job), grouped by worker. Overlap is
	///     raw wall-clock intersection of recorded sessions (spec §4.4 permits one worker's sessions on
	///     different leaves to overlap deliberately, and §10.2 makes that overlap the cost engine's
	///     concurrency divisor), never an allocated or costed figure: no schedule, working-time
	///     eligibility, or rate enters into it. A node with no sessions of its own — a branch, or an
	///     unworked leaf — returns no rows rather than throwing. Carries no ownership-based
	///     authorization gate beyond the baseline-employee admission every general job/work read shares:
	///     recorded work is job data every employee role may read (ADR 0041).
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor holds no baseline operational role permitted to browse job data.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The actor or the job node does not exist.</exception>
	Task<ConcurrentWorkResult> GetConcurrentWorkAsync(
		GetConcurrentWorkRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves sessions on a leaf, most recent first (plan §8.5 slice 4). A
	///     <see langword="null" /> <see cref="GetLeafSessionsRequest.WorkedByUserId" /> returns every
	///     worker's sessions; setting it filters the read to that worker. Recorded work is job data that
	///     every operational employee may view regardless of worker or node control (ADR 0041; see
	///     <see cref="Domain.Authorization.WorkSessionAccessPolicy.CanView" />).
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor holds no operational employee role permitted to view job data.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The leaf does not exist.</exception>
	Task<EquatableArray<WorkSessionResult>> GetLeafSessionsAsync(
		GetLeafSessionsRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves every worker's unfinished sessions among the given leaves, for the plural active-
	///     session presentation on job-tree browsing (ADR 0041; browse-sessions plan §2.4), mirroring
	///     <see cref="GetJobSummariesAsync" />'s batch-by-ids shape so rendering performs no per-row
	///     lookup. Never throws for a leaf id that no longer resolves — see
	///     <see cref="GetJobSummariesRequest" />.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor holds no operational employee role permitted by
	///     <see cref="Domain.Authorization.WorkSessionAccessPolicy.CanView" />.
	/// </exception>
	Task<EquatableArray<WorkSessionResult>> GetActiveSessionsAsync(
		GetActiveSessionsRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves, for each of the given leaves, whether the actor may currently manage sessions on
	///     it (ADR 0044 Stage 4: a batched rendering capability, one round trip regardless of leaf
	///     count, backing the "Start for…" disclosure and authorized other-worker finish action). Never
	///     throws for a leaf id that no longer resolves — see <see cref="GetJobSummariesRequest" />. This
	///     is a rendering hint only; the authoritative gate remains each command's own re-check.
	/// </summary>
	/// <exception cref="EntityNotFoundException">The actor does not exist.</exception>
	Task<EquatableArray<LeafSessionManageCapabilityResult>> GetSessionManageCapabilitiesAsync(
		GetSessionManageCapabilitiesRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves a leaf's current <c>LeafWork</c> (plan §8.5 slice 5). Carries no ownership-based
	///     authorization gate (see <see cref="GetReadinessRequest" />) beyond the baseline-employee
	///     admission every general job/work read shares (remediation plan §2.4).
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor holds no baseline operational role permitted to browse job data.
	/// </exception>
	/// <exception cref="EntityNotFoundException">
	///     The actor does not exist, or the job node has no <c>LeafWork</c> attached.
	/// </exception>
	Task<LeafWorkResult> GetLeafWorkAsync(GetLeafWorkRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves every prerequisite edge touching a node, in either direction (plan §8.5 slice 5).
	///     Carries no ownership-based authorization gate (see <see cref="GetReadinessRequest" />)
	///     beyond the baseline-employee admission every general job/work read shares (remediation
	///     plan §2.4).
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor holds no baseline operational role permitted to browse job data.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The actor or the node does not exist.</exception>
	Task<EquatableArray<PrerequisiteEdge>> GetPrerequisitesAsync(
		GetPrerequisitesRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves a bounded multi-level subtree rooted at <see cref="GetJobSubtreeRequest.RootId" />
	///     (ADR 0039): every immediate child of the root, and for every node whose children are expanded
	///     to a further level, only the first <see cref="Domain.Hierarchy.JobSubtreeLimits.BreadthCap" />
	///     children (by <c>Id</c> order) recurse further. Structure carries no ownership-based
	///     authorization gate, matching <see cref="GetJobChildrenAsync" />, beyond the baseline-employee
	///     admission every general job/work read shares (remediation plan §2.4); the cost roll-up
	///     (<see cref="JobSubtreeResult.RootTotal" />/<see cref="JobSubtreeNodeResult.Cost" />) is
	///     individually gated by <see cref="Domain.Authorization.CostAccessPolicy" /> (ADR 0040) and
	///     simply omitted, never denying the whole request, when the actor may not view it.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor holds no baseline operational role permitted to browse job data.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The actor or the root node does not exist.</exception>
	/// <exception cref="ArgumentOutOfRangeException">
	///     <see cref="GetJobSubtreeRequest.MaxDepth" /> is negative or exceeds
	///     <see cref="Domain.Hierarchy.JobSubtreeLimits.HardMaxDepth" />.
	/// </exception>
	Task<JobSubtreeResult> GetJobSubtreeAsync(GetJobSubtreeRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Derives a branch's (or the root's) rollup achievement from its complete descendant subtree:
	///     <see cref="BranchAchievement.Success" /> iff every leaf, at any depth, has succeeded,
	///     recursively through any nested branches; <see cref="BranchAchievement.Unfinished" /> otherwise.
	///     Carries no ownership-based authorization gate (see <see cref="GetJobNodeAsync" />) beyond
	///     the baseline-employee admission every general job/work read shares (remediation plan §2.4).
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor holds no baseline operational role permitted to browse job data.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The actor or the node does not exist.</exception>
	Task<BranchAchievement> GetBranchAchievementAsync(
		GetBranchAchievementRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves an employee's schedule versions and exceptions (plan §8.5 slice 6). The actor may
	///     always view their own; viewing another employee's requires <see cref="EmployeeRole.Administrator" />
	///     (see <see cref="Domain.Authorization.ScheduleAccessPolicy" />).
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not view <see cref="GetScheduleRequest.UserId" />'s schedule.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The target employee does not exist.</exception>
	Task<ScheduleSnapshotResult> GetScheduleAsync(GetScheduleRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves an employee's user cost rates and node rate overrides (plan §8.5 slice 7). Unlike
	///     <see cref="GetScheduleAsync" />, there is no self-view carve-out — every actor is gated by
	///     <see cref="Domain.Authorization.CostAccessPolicy" /> uniformly (see <see cref="GetRatesRequest" />).
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor does not hold <see cref="EmployeeRole.Administrator" /> or <see cref="EmployeeRole.CostViewer" />.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The target employee does not exist.</exception>
	Task<RateSnapshotResult> GetRatesAsync(GetRatesRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves the unified <c>/Jobs/Work</c> page's single bounded projection (unified-leaf-workflow
	///     plan Stage 4): node context, leaf achievement/version, readiness, every active session (never
	///     collapsed, ADR 0041), dependent-impact count, and actor-specific action capabilities for the
	///     new atomic composites, in one call regardless of session or history growth. Carries no
	///     ownership-based authorization gate of its own (see <see cref="GetReadinessRequest" />)
	///     beyond the baseline-employee admission every general job/work read shares (remediation
	///     plan §2.4); the <c>Can*</c> members are rendering hints only, never authoritative.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor holds no baseline operational role permitted to browse job data.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The actor or the node does not exist.</exception>
	Task<LeafWorkPageResult> GetLeafWorkPageAsync(GetLeafWorkPageRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Measures exactly what <see cref="IJobCommands.DeleteSubtreeAsync" /> rooted at
	///     <see cref="SubtreeImpactRequest.RootId" /> would destroy (ADR 0061), backing the
	///     confirmation screen. Counts are exact over the whole subtree, deliberately not reusing the
	///     depth/breadth-capped Browse subtree query, which would under-report. Read-only and
	///     Administrator-gated, matching the command it previews.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor does not hold <see cref="EmployeeRole.Administrator" />.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The actor or the root node does not exist.</exception>
	Task<SubtreeImpactResult> GetSubtreeImpactAsync(SubtreeImpactRequest request, CancellationToken cancellationToken = default);
}
