namespace JobTrack.Domain.Authorization;

using Abstractions;

/// <summary>
///     Pure authorization rules for requester intake (ADR 0033; plan §6). Holding-area eligibility,
///     department membership, and node control are tree/reference facts only persistence can compute,
///     so callers supply them rather than this policy fetching them itself — the same fresh-reload rule
///     as <see cref="JobNodeAccessPolicy" />/<see cref="WorkSessionAccessPolicy" />.
/// </summary>
public static class RequesterAccessPolicy
{
	/// <summary>
	///     An actor may submit a request into a holding area if they hold
	///     <see cref="EmployeeRole.Requester" />, the holding area is active, and the actor is eligible
	///     for that holding area (department routing or global eligibility, ADR 0033 §3). No other role
	///     may submit through this path.
	/// </summary>
	public static bool CanSubmit(IReadOnlyCollection<EmployeeRole> actorRoles, RequesterSubmissionFacts facts)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);
		ArgumentNullException.ThrowIfNull(facts);

		return actorRoles.Contains(EmployeeRole.Requester) && facts.IsHoldingAreaActive && facts.ActorIsEligibleForHoldingArea;
	}

	/// <summary>
	///     An actor may view a requester-safe progress projection if they are the request's own
	///     requester, or (when department visibility is enabled) they hold
	///     <see cref="EmployeeRole.Requester" /> and share the request's department, or they hold
	///     <see cref="EmployeeRole.Administrator" /> or <see cref="EmployeeRole.JobManager" />, or they
	///     hold <see cref="EmployeeRole.Worker" /> and control the request's anchor node.
	/// </summary>
	public static bool CanView(IReadOnlyCollection<EmployeeRole> actorRoles, RequesterVisibilityFacts facts)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);
		ArgumentNullException.ThrowIfNull(facts);

		return facts.ActorIsRequestOwner
			   || facts.IsDepartmentVisibilityEnabled && actorRoles.Contains(EmployeeRole.Requester) && facts.ActorSharesRequestDepartment
			   || actorRoles.Contains(EmployeeRole.Administrator)
			   || actorRoles.Contains(EmployeeRole.JobManager)
			   || actorRoles.Contains(EmployeeRole.Worker) && facts.ActorControlsAnchorNode;
	}

	/// <summary>
	///     An actor may add a requester-visible comment/clarification if <see cref="CanView" /> holds,
	///     they hold <see cref="EmployeeRole.Requester" />, and the request is still open to the
	///     requester. Staff notes are a separate, non-requester channel and are not governed by this
	///     method.
	/// </summary>
	public static bool CanCommentAsRequester(IReadOnlyCollection<EmployeeRole> actorRoles, RequesterCommentFacts facts)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);
		ArgumentNullException.ThrowIfNull(facts);

		return CanView(actorRoles, facts.Visibility)
			   && actorRoles.Contains(EmployeeRole.Requester)
			   && facts.IsOpenToRequester;
	}
}
