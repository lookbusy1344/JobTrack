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
	///     is in the future (<c>ConstraintId</c> <c>"work-session-start-in-future"</c>, ADR 0028); a
	///     supplied <see cref="StartSessionRequest.StartedAt" /> would overlap another session for the
	///     same worker and leaf (<c>ConstraintId</c> <c>"work-session-overlap"</c>); the leaf is
	///     currently closed to new sessions — terminal achievement or archived (<c>ConstraintId</c>
	///     <c>"work-session-leaf-closed"</c>, ADR 0044); or <see cref="StartSessionRequest.WorkedByUserId" />
	///     names a worker who is disabled, locked, or holds no eligible workflow role (<c>ConstraintId</c>
	///     <c>"work-session-target-not-eligible"</c>, ADR 0044 Stage 6).
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
	///     in the future (<c>ConstraintId</c> <c>"work-session-start-in-future"</c>); a supplied
	///     <see cref="StartWorkRequest.StartedAt" /> would overlap another session for the same worker
	///     and leaf (<c>ConstraintId</c> <c>"work-session-overlap"</c>); the leaf is currently closed
	///     to new sessions — terminal achievement or archived (<c>ConstraintId</c>
	///     <c>"work-session-leaf-closed"</c>, ADR 0044); or <see cref="StartWorkRequest.WorkedByUserId" />
	///     names a worker who is disabled, locked, or holds no eligible workflow role (<c>ConstraintId</c>
	///     <c>"work-session-target-not-eligible"</c>, ADR 0044 Stage 6).
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

	/// <summary>
	///     Atomic composite (remediation plan §2.1): finishes the active session named by
	///     <see cref="FinishSessionAndUpdateWriteUpRequest.SessionId" /> and applies an optional write-up
	///     change to its leaf's node, in one commit -- so a submitted write-up cannot commit when the
	///     finish it accompanied is rejected, nor can the finish commit while silently discarding the
	///     write-up. A caller with no write-up to change (<see cref="FinishSessionAndUpdateWriteUpRequest.WriteUpChange" />
	///     null) still uses this command's fixed shape rather than the plain <see cref="FinishSessionAsync" />
	///     primitive gaining conditional UI-orchestration semantics.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage this session (see <see cref="Domain.Authorization.WorkSessionAccessPolicy" />),
	///     or -- when a write-up change is supplied -- may not edit the leaf's node
	///     (see <see cref="Domain.Authorization.JobNodeAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The session does not exist.</exception>
	/// <exception cref="ConcurrencyConflictException">
	///     The supplied session <see cref="FinishSessionAndUpdateWriteUpRequest.Version" /> is stale, or a
	///     supplied <see cref="FinishSessionAndUpdateWriteUpRequest.WriteUpChange" />'s <c>NodeVersion</c>
	///     is stale.
	/// </exception>
	/// <exception cref="InvariantViolationException">
	///     A supplied <see cref="FinishSessionAndUpdateWriteUpRequest.FinishedAt" /> is not after the
	///     session's start instant (<c>ConstraintId</c> <c>"work-session-invalid-interval"</c>) or is in
	///     the future (<c>ConstraintId</c> <c>"work-session-finish-in-future"</c>, ADR 0028).
	/// </exception>
	Task<FinishSessionAndUpdateWriteUpResult> FinishSessionAndUpdateWriteUpAsync(
		FinishSessionAndUpdateWriteUpRequest request, CancellationToken cancellationToken = default);

	/// <summary>Corrects a historical session's start and/or finish instants (spec §4.4).</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not manage this session (see <see cref="Domain.Authorization.WorkSessionAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The session does not exist.</exception>
	/// <exception cref="ConcurrencyConflictException">The supplied version is stale.</exception>
	/// <exception cref="InvariantViolationException">
	///     The corrected interval is invalid (<c>ConstraintId</c> <c>"work-session-invalid-interval"</c>);
	///     it would overlap another session for the same worker and leaf (<c>ConstraintId</c>
	///     <c>"work-session-overlap"</c>); or it would leave the session active while the leaf is
	///     currently closed — terminal achievement or archived (<c>ConstraintId</c>
	///     <c>"work-session-leaf-closed"</c>, ADR 0044); a correction that keeps an already-finished
	///     session finished remains permitted on a closed leaf.
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
	///     <c>"achievement-transition-not-permitted"</c>, ADR 0001); or the transition enters a terminal
	///     state while a session on the leaf is still active (<c>ConstraintId</c>
	///     <c>"leaf-closure-active-sessions"</c>, ADR 0044).
	/// </exception>
	/// <exception cref="PrerequisiteBlockedException">
	///     The transition enters a completed state while the leaf's prerequisites are unsatisfied (spec §6).
	/// </exception>
	Task<LeafWorkResult> SetAchievementAsync(SetAchievementRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Atomic composite (ADR 0045 §1/§3, ADR 0047): finishes the exact, caller-confirmed
	///     active-session set named by <see cref="CompleteLeafRequest.ExpectedActiveSessions" /> (zero,
	///     one, or many) at one captured instant, and transitions the leaf
	///     <see cref="Achievement.InProgress" /> -&gt; <see cref="CompleteLeafRequest.FinalAchievement" />
	///     (<see cref="Achievement.Success" /> by default, or <see cref="Achievement.Cancelled" />/
	///     <see cref="Achievement.Unsuccessful" />) with a fixed structured reason, in one commit. Ordinary
	///     <see cref="FinishSessionAsync" /> gains no implicit meaning from this addition: finishing a
	///     session never implies any particular outcome. A caller not choosing to end active sessions and
	///     an achievement together still uses <see cref="SetAchievementAsync" /> directly.
	///     <see cref="CompleteLeafRequest.WriteUpChange" /> optionally applies a write-up change to the
	///     leaf's node in the same commit (remediation plan §2.1).
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor may not complete this leaf (the same authority <see cref="SetAchievementAsync" />
	///     already requires for the terminal transition -- controlling owner, Job Manager, or
	///     Administrator; see <see cref="Domain.Authorization.AchievementAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The leaf has no <c>LeafWork</c> attached.</exception>
	/// <exception cref="ConcurrencyConflictException">
	///     The supplied leaf <see cref="CompleteLeafRequest.Version" /> is stale, the leaf's current
	///     active-session set no longer matches <see cref="CompleteLeafRequest.ExpectedActiveSessions" />
	///     exactly, by id and version (ADR 0045 §3), or a supplied
	///     <see cref="CompleteLeafRequest.WriteUpChange" />'s <c>NodeVersion</c> is stale.
	/// </exception>
	/// <exception cref="InvariantViolationException">
	///     The leaf's achievement is not <see cref="Achievement.InProgress" />, or
	///     <see cref="CompleteLeafRequest.FinalAchievement" /> is not one of <see cref="Achievement.Success" />/
	///     <see cref="Achievement.Cancelled" />/<see cref="Achievement.Unsuccessful" /> (<c>ConstraintId</c>
	///     <c>"achievement-transition-not-permitted"</c>, ADR 0001 -- <c>Waiting -&gt; Success</c> remains
	///     prohibited); a supplied <see cref="CompleteLeafRequest.FinishedAt" /> is not after every
	///     affected session's start instant (<c>ConstraintId</c> <c>"work-session-invalid-interval"</c>)
	///     or is in the future (<c>ConstraintId</c> <c>"work-session-finish-in-future"</c>, ADR 0028).
	/// </exception>
	/// <exception cref="PrerequisiteBlockedException">The leaf's prerequisites are not satisfied (spec §6).</exception>
	Task<CompleteLeafResult> CompleteLeafAsync(CompleteLeafRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Atomic composite (ADR 0045 §1/§2): transitions a terminal leaf back to
	///     <see cref="Achievement.Waiting" /> with <see cref="ReopenAndStartWorkRequest.Reason" />,
	///     applies ADR 0038's existing <see cref="Achievement.Waiting" /> -&gt;
	///     <see cref="Achievement.InProgress" /> auto-advance, and starts
	///     <see cref="ReopenAndStartWorkRequest.WorkedByUserId" />'s session, in one commit. Authorized
	///     more widely than <see cref="SetAchievementAsync" />'s reopening path: a controlling owner, Job
	///     Manager, or Administrator may start for any eligible target worker, and a prior session
	///     participant on this leaf who controls nothing may start for themselves only (ADR 0045 §2).
	///     <c>ReopenWithoutStartingAsync</c>-shaped elevated correction (no session following) is
	///     unaffected and remains Job Manager/Administrator-only via <see cref="SetAchievementAsync" />.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor holds none of ADR 0045 §2's three reopen-and-start authority sources for this leaf
	///     and target worker (see <see cref="Domain.Authorization.LeafReopenAndStartAccessPolicy" />).
	/// </exception>
	/// <exception cref="EntityNotFoundException">The leaf has no <c>LeafWork</c> attached.</exception>
	/// <exception cref="ConcurrencyConflictException">The supplied <see cref="ReopenAndStartWorkRequest.Version" /> is stale.</exception>
	/// <exception cref="InvariantViolationException">
	///     The leaf's achievement is not currently terminal (<c>ConstraintId</c>
	///     <c>"achievement-transition-not-permitted"</c>, ADR 0001); the leaf's node is archived
	///     (<c>ConstraintId</c> <c>"work-session-leaf-closed"</c>, ADR 0044 -- restore the node first); a
	///     supplied <see cref="ReopenAndStartWorkRequest.StartedAt" /> is in the future (<c>ConstraintId</c>
	///     <c>"work-session-start-in-future"</c>) or would overlap another session for the same worker
	///     and leaf (<c>ConstraintId</c> <c>"work-session-overlap"</c>); or
	///     <see cref="ReopenAndStartWorkRequest.WorkedByUserId" /> names a worker who is disabled, locked,
	///     or holds no eligible workflow role (<c>ConstraintId</c> <c>"work-session-target-not-eligible"</c>).
	/// </exception>
	Task<ReopenAndStartWorkResult> ReopenAndStartWorkAsync(ReopenAndStartWorkRequest request, CancellationToken cancellationToken = default);
}
