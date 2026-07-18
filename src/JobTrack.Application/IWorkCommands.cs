namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Work-session and achievement commands (plan §7.3 steps 6–7: start, finish, resume, and correct
///     work sessions, with pause and stop treated as UI terms for finishing the active session; change
///     achievement subject to prerequisite gates; docs/api/jobtrack-client-design.md).
/// </summary>
public interface IWorkCommands
{
	/// <summary>
	///     Starts a new session. A UI "resume" action is this same command called again for the same
	///     leaf and worker, not a distinct operation (spec §4.4). <see cref="StartSessionRequest.StartedAt" />
	///     may supply a past instant to record a session that already started (ADR 0028).
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage this session (see <see cref="Domain.Authorization.WorkSessionAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The leaf has no <c>LeafWork</c> attached.</exception>
	/// <exception cref="InvariantViolationException">
	///     The worker already has an active session for this leaf (<c>ConstraintId</c>
	///     <c>"work-session-already-active"</c>, spec §4.4); a supplied <see cref="StartSessionRequest.StartedAt" />
	///     is in the future (<c>ConstraintId</c> <c>"work-session-start-in-future"</c>, ADR 0028); or a
	///     supplied <see cref="StartSessionRequest.StartedAt" /> would overlap another session for the
	///     same worker and leaf (<c>ConstraintId</c> <c>"work-session-overlap"</c>).
	/// </exception>
	/// <exception cref="PrerequisiteBlockedException">The leaf's prerequisites are not satisfied (spec §6).</exception>
	Task<WorkSessionResult> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     One-click composite: attaches <c>LeafWork</c> if <see cref="StartWorkRequest.JobNodeId" />
	///     doesn't already have it, advances a freshly-<see cref="Achievement.Waiting" /> leaf to
	///     <see cref="Achievement.InProgress" /> (idempotent -- a no-op if it is already
	///     <see cref="Achievement.InProgress" />, e.g. from another worker's active session), and starts
	///     a session, all inside one transaction -- so a failure at any step (blocked prerequisite,
	///     already-active session for this worker) leaves no partial state. A UI "resume" action is this
	///     same command called again, not a distinct operation (spec §4.4).
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage this node (see <see cref="Domain.Authorization.JobNodeAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The job node does not exist.</exception>
	/// <exception cref="InvariantViolationException">
	///     The node is the root or has children, so it cannot hold <c>LeafWork</c> (<c>ConstraintId</c>
	///     <c>"job-node-is-root-cannot-attach-leaf-work"</c> or <c>"job-node-has-children-cannot-attach-leaf-work"</c>);
	///     the worker already has an active session for this leaf (<c>ConstraintId</c>
	///     <c>"work-session-already-active"</c>); a supplied <see cref="StartWorkRequest.StartedAt" /> is
	///     in the future (<c>ConstraintId</c> <c>"work-session-start-in-future"</c>); or a supplied
	///     <see cref="StartWorkRequest.StartedAt" /> would overlap another session for the same worker
	///     and leaf (<c>ConstraintId</c> <c>"work-session-overlap"</c>).
	/// </exception>
	/// <exception cref="PrerequisiteBlockedException">The leaf's prerequisites are not satisfied (spec §6).</exception>
	Task<WorkSessionResult> StartWorkAsync(StartWorkRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Finishes the active session. "Pause" and "stop" are UI descriptions of this same operation
	///     (spec §4.4). Remains possible even after the leaf's prerequisites have regressed.
	///     <see cref="FinishSessionRequest.FinishedAt" /> may supply a past instant to record when the
	///     session actually finished (ADR 0028).
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage this session (see <see cref="Domain.Authorization.WorkSessionAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The session does not exist.</exception>
	/// <exception cref="ConcurrencyConflictException">The supplied version is stale.</exception>
	/// <exception cref="InvariantViolationException">
	///     A supplied <see cref="FinishSessionRequest.FinishedAt" /> is not after the session's start
	///     instant (<c>ConstraintId</c> <c>"work-session-invalid-interval"</c>) or is in the future
	///     (<c>ConstraintId</c> <c>"work-session-finish-in-future"</c>, ADR 0028).
	/// </exception>
	Task<WorkSessionResult> FinishSessionAsync(FinishSessionRequest request, CancellationToken cancellationToken = default);

	/// <summary>Corrects a historical session's start and/or finish instants (spec §4.4).</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage this session (see <see cref="Domain.Authorization.WorkSessionAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The session does not exist.</exception>
	/// <exception cref="ConcurrencyConflictException">The supplied version is stale.</exception>
	/// <exception cref="InvariantViolationException">
	///     The corrected interval is invalid (<c>ConstraintId</c> <c>"work-session-invalid-interval"</c>)
	///     or would overlap another session for the same worker and leaf (<c>ConstraintId</c>
	///     <c>"work-session-overlap"</c>).
	/// </exception>
	Task<WorkSessionResult> CorrectSessionAsync(CorrectSessionRequest request, CancellationToken cancellationToken = default);

	/// <summary>Transitions a leaf's <c>LeafWork</c> achievement (ADR 0001).</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage this node's subtree, or is attempting to reopen a terminal state
	///     without <see cref="EmployeeRole.Administrator" /> or <see cref="EmployeeRole.JobManager" />
	///     (see <see cref="Domain.Authorization.AchievementAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The leaf has no <c>LeafWork</c> attached.</exception>
	/// <exception cref="ConcurrencyConflictException">The supplied version is stale.</exception>
	/// <exception cref="InvariantViolationException">
	///     The requested transition is not permitted from the current state (<c>ConstraintId</c>
	///     <c>"achievement-transition-not-permitted"</c>, ADR 0001).
	/// </exception>
	/// <exception cref="PrerequisiteBlockedException">
	///     The transition enters a completed state while the leaf's prerequisites are unsatisfied (spec §6).
	/// </exception>
	Task<LeafWorkResult> SetAchievementAsync(SetAchievementRequest request, CancellationToken cancellationToken = default);
}
