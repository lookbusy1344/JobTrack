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
	///     disabled accounts — mirrors the existing web-layer workflow-employee dropdown filter.
	///     Carries no authorization gate of its own (see <see cref="EmployeeDirectoryEntry" />).
	/// </summary>
	Task<EquatableArray<EmployeeDirectoryEntry>> GetEmployeeDirectoryAsync(
		GetEmployeeDirectoryRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves every employee's directory-visible identity (display name and login username) —
	///     see <see cref="EmployeeDirectoryEntry" /> — across every <see cref="EmployeeRole" /> and
	///     enabled state, unlike <see cref="GetEmployeeDirectoryAsync" />'s workflow-only,
	///     enabled-only scope. For admin lookups that target any employee (rota, rates, role
	///     assignment, account management), where a disabled or non-workflow account must still be
	///     findable by name. Carries no authorization gate of its own (see
	///     <see cref="EmployeeDirectoryEntry" />).
	/// </summary>
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
	///     authorization gate (see <see cref="GetReadinessRequest" />) — viewing job data is an
	///     unqualified baseline capability for every role (spec §7.3), unlike employee-account data.
	/// </summary>
	/// <exception cref="EntityNotFoundException">The node does not exist.</exception>
	Task<ReadinessResult> GetReadinessAsync(GetReadinessRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves a node's full detail and root-first ancestor breadcrumb. Carries no
	///     ownership-based authorization gate (see <see cref="GetReadinessRequest" />) — viewing job
	///     data is an unqualified baseline capability for every role (spec §7.3).
	/// </summary>
	/// <exception cref="EntityNotFoundException">The node does not exist.</exception>
	Task<JobNodeDetailResult> GetJobNodeAsync(GetJobNodeRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves a node's direct children, filtered by owner and archive scope. Carries no
	///     ownership-based authorization gate (see <see cref="GetJobNodeAsync" />).
	/// </summary>
	/// <exception cref="EntityNotFoundException">The parent node does not exist.</exception>
	Task<EquatableArray<JobNodeSummaryResult>> GetJobChildrenAsync(
		GetJobChildrenRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Searches every node's description for a case-insensitive substring match, filtered by owner
	///     and archive scope. Carries no ownership-based authorization gate (see
	///     <see cref="GetJobNodeAsync" />).
	/// </summary>
	/// <exception cref="ArgumentException"><see cref="SearchJobNodesRequest.SearchText" /> is blank.</exception>
	Task<EquatableArray<JobNodeSummaryResult>> SearchJobNodesAsync(
		SearchJobNodesRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Describes whatever subset of <see cref="GetJobSummariesRequest.NodeIds" /> currently resolves
	///     to a node, archived or not. Carries no ownership-based authorization gate (see
	///     <see cref="GetJobNodeAsync" />) and, unlike it, never throws for an id that no longer
	///     resolves — see <see cref="GetJobSummariesRequest" />.
	/// </summary>
	Task<EquatableArray<JobNodeSummaryResult>> GetJobSummariesAsync(
		GetJobSummariesRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves the flat "jobs awaiting progress" list: leaves only — never a branch or the root —
	///     that are <see cref="Achievement.Waiting" /> or <see cref="Achievement.InProgress" />, not
	///     archived, and ready per <see cref="GetReadinessAsync" />'s <see cref="ReadinessCalculator" />
	///     (a blocked leaf is not actionable, so it does not belong on a work queue), optionally scoped
	///     to one owner and/or one subtree, ordered by descending priority then ascending deadline.
	///     Carries no ownership-based authorization gate (see <see cref="GetJobNodeAsync" />).
	/// </summary>
	/// <exception cref="EntityNotFoundException">
	///     <see cref="GetAwaitingProgressRequest.SubtreeRootId" /> is set and does not exist.
	/// </exception>
	Task<EquatableArray<AwaitingProgressEntry>> GetAwaitingProgressAsync(
		GetAwaitingProgressRequest request, CancellationToken cancellationToken = default);

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
	///     authorization gate (see <see cref="GetReadinessRequest" />) — viewing job data, including
	///     achievement state, is an unqualified baseline capability for every role (spec §7.3).
	/// </summary>
	/// <exception cref="EntityNotFoundException">The job node has no <c>LeafWork</c> attached.</exception>
	Task<LeafWorkResult> GetLeafWorkAsync(GetLeafWorkRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves every prerequisite edge touching a node, in either direction (plan §8.5 slice 5).
	///     Carries no ownership-based authorization gate (see <see cref="GetReadinessRequest" />).
	/// </summary>
	/// <exception cref="EntityNotFoundException">The node does not exist.</exception>
	Task<EquatableArray<PrerequisiteEdge>> GetPrerequisitesAsync(
		GetPrerequisitesRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Retrieves a bounded multi-level subtree rooted at <see cref="GetJobSubtreeRequest.RootId" />
	///     (ADR 0039): every immediate child of the root, and for every node whose children are expanded
	///     to a further level, only the first <see cref="Domain.Hierarchy.JobSubtreeLimits.BreadthCap" />
	///     children (by <c>Id</c> order) recurse further. Structure carries no ownership-based
	///     authorization gate, matching <see cref="GetJobChildrenAsync" />; the cost roll-up
	///     (<see cref="JobSubtreeResult.RootTotal" />/<see cref="JobSubtreeNodeResult.Cost" />) is
	///     individually gated by <see cref="Domain.Authorization.CostAccessPolicy" /> (ADR 0040) and
	///     simply omitted, never denying the whole request, when the actor may not view it.
	/// </summary>
	/// <exception cref="EntityNotFoundException">The root node does not exist.</exception>
	/// <exception cref="ArgumentOutOfRangeException">
	///     <see cref="GetJobSubtreeRequest.MaxDepth" /> is negative or exceeds
	///     <see cref="Domain.Hierarchy.JobSubtreeLimits.HardMaxDepth" />.
	/// </exception>
	Task<JobSubtreeResult> GetJobSubtreeAsync(GetJobSubtreeRequest request, CancellationToken cancellationToken = default);

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
	///     ownership-based authorization gate of its own (see <see cref="GetReadinessRequest" />) --
	///     viewing job data is an unqualified baseline capability for every role (spec §7.3); the
	///     <c>Can*</c> members are rendering hints only, never authoritative.
	/// </summary>
	/// <exception cref="EntityNotFoundException">The node does not exist.</exception>
	Task<LeafWorkPageResult> GetLeafWorkPageAsync(GetLeafWorkPageRequest request, CancellationToken cancellationToken = default);
}
