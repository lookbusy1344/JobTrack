namespace JobTrack.Application;

using Abstractions;
using Domain.Authorization;

/// <summary>Requester intake commands: submit a request into a configured holding area (ADR 0033).</summary>
public interface IRequestCommands
{
	/// <summary>
	///     Submits a new request as a direct child of <see cref="SubmitJobRequestRequest.HoldingAreaId" />'s
	///     configured job node. The new node's parent, owner, kind, priority, posted-by user, and
	///     timestamps are never caller-supplied — parent and kind/priority defaults come from the
	///     holding area's own configuration, the owner defaults to the holding area's configured default
	///     owner (or unassigned), posted-by is always the acting requester, and timestamps are
	///     server-generated.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor does not hold <see cref="EmployeeRole.Requester" />, the holding area is
	///     inactive, or the actor is not eligible for this holding area.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The holding area does not exist.</exception>
	Task<JobRequestResult> SubmitAsync(SubmitJobRequestRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Moves a requester-originated node (one with an associated <c>job_request</c> row) to a new
	///     parent (ADR 0033, plan §5). Authorization is <c>canMoveRequesterJob</c>: the actor must hold
	///     <see cref="EmployeeRole.Administrator" /> or <see cref="EmployeeRole.JobManager" />, or hold
	///     <see cref="EmployeeRole.Worker" /> and control <see cref="MoveRequesterJobRequest.NodeId" /> —
	///     deliberately not also control of <see cref="MoveRequesterJobRequest.NewParentId" />, unlike
	///     <see cref="IJobCommands.MoveAsync" />. Every other hierarchy/workflow invariant (permanent root,
	///     cycle, no <c>LeafWork</c> parent, prerequisite ancestor/descendant check, optimistic
	///     concurrency, audit) still applies unchanged. The <c>job_request</c> row's own
	///     <c>holding_area_id</c>/<c>requester_user_id</c> are untouched by a move.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">The actor may not move this node under <c>canMoveRequesterJob</c>.</exception>
	/// <exception cref="EntityNotFoundException">The node does not exist.</exception>
	/// <exception cref="InvariantViolationException">
	///     The node has no associated <c>job_request</c> row, or the move violates a structural invariant.
	/// </exception>
	/// <exception cref="ConcurrencyConflictException">The supplied version does not match the node's current version.</exception>
	Task<JobNodeResult> MoveAsync(MoveRequesterJobRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Lists <paramref name="context" />'s own submitted requests, most recent first (ADR 0033, plan
	///     §8 <c>/Requests</c>). Scoped to <c>job_request.requester_user_id = context.Actor</c> — a
	///     dedicated, narrow query, never a relaxation of <c>/Jobs/Browse</c> or another operational
	///     query's authorization.
	/// </summary>
	Task<EquatableArray<JobRequestSummaryResult>> GetMyRequestsAsync(CommandContext context, CancellationToken cancellationToken = default);

	/// <summary>
	///     Lists the active holding areas <paramref name="context" />'s actor is currently eligible to
	///     submit into (ADR 0033, plan §3): globally eligible holding areas (<c>department_id IS NULL</c>)
	///     plus any scoped to a department the actor belongs to. Does not require
	///     <see cref="EmployeeRole.Requester" /> itself — eligibility is department membership, evaluated
	///     the same way regardless of role, matching <see cref="RequesterAccessPolicy.CanSubmit" />'s own
	///     eligibility input.
	/// </summary>
	Task<EquatableArray<HoldingAreaSummaryResult>> GetEligibleHoldingAreasAsync(
		CommandContext context, CancellationToken cancellationToken = default);

	/// <summary>
	///     Sets <c>job_request.acknowledged_at</c>/<c>acknowledged_by_user_id</c> once, giving the
	///     requester an explicit <see cref="RequesterStatus.Accepted" /> signal (ADR 0034). Authorized the
	///     same way as <see cref="MoveAsync" />: the actor must hold <see cref="EmployeeRole.Administrator" />
	///     or <see cref="EmployeeRole.JobManager" />, or hold <see cref="EmployeeRole.Worker" /> and control
	///     <see cref="AcknowledgeJobRequestRequest.NodeId" />.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">The actor may not acknowledge this node.</exception>
	/// <exception cref="EntityNotFoundException">The node does not exist.</exception>
	/// <exception cref="InvariantViolationException">The node has no associated <c>job_request</c> row.</exception>
	/// <exception cref="ConcurrencyConflictException">The supplied version does not match the request's current version.</exception>
	Task<JobRequestResult> AcknowledgeAsync(AcknowledgeJobRequestRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Appends a note to a request's notes thread (ADR 0034), authored either by staff (same
	///     authorization as <see cref="MoveAsync" />) or by the request's own requester (
	///     <see cref="RequesterAccessPolicy.CanCommentAsRequester" />). A requester-authored note is always
	///     visible to the requester, regardless of <see cref="AddJobRequestNoteRequest.VisibleToRequester" />.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor is neither staff who may manage this node nor the request's own requester with an
	///     open (not closed-to-requester) request.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The node does not exist.</exception>
	/// <exception cref="InvariantViolationException">The node has no associated <c>job_request</c> row.</exception>
	Task<JobRequestNoteResult> AddNoteAsync(AddJobRequestNoteRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Returns one request's requester-safe detail projection (ADR 0034, plan §7/§8): status, the
	///     read-only subtree, and the notes visible to the calling actor
	///     (<see cref="RequesterAccessPolicy.CanView" />). A requester caller sees only requester-visible
	///     notes; a staff/admin caller sees every note.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">The actor may not view this request.</exception>
	/// <exception cref="EntityNotFoundException">The node does not exist.</exception>
	/// <exception cref="InvariantViolationException">The node has no associated <c>job_request</c> row.</exception>
	Task<JobRequestDetailResult> GetDetailAsync(GetJobRequestDetailRequest request, CancellationToken cancellationToken = default);
}
