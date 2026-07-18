namespace JobTrack.Domain.Authorization;

using Abstractions;

/// <summary>
///     Pure authorization rules for work sessions (plan §7.3 step 6; ownership model §4.2/§4; ADR 0032).
///     <see cref="CanManage" /> gates starting, finishing, and correcting a session; <see cref="CanView" />
///     gates listing sessions and is unaffected by node ownership. Whether the actor controls the node
///     (or, for viewing, the session is their own) is a persistence fact supplied by the caller, the
///     same fresh-reload rule as <see cref="EmployeeAccessPolicy" />/<see cref="JobNodeAccessPolicy" />.
/// </summary>
public static class WorkSessionAccessPolicy
{
	/// <summary>
	///     An actor may start, finish, or correct a work session if they hold
	///     <see cref="EmployeeRole.Administrator" /> or <see cref="EmployeeRole.JobManager" />, or hold
	///     <see cref="EmployeeRole.Worker" /> and control the leaf's node — directly own it or an
	///     ancestor (ownership model §3/§4.2; ADR 0032). This replaces the pre-ownership-model
	///     self-session rule: a controlling owner may record a session for any
	///     <c>worked_by_user_id</c>, and a Worker who controls nothing on the tree may record no work
	///     there, not even their own, until they pick up a node (ownership model §4.3).
	/// </summary>
	public static bool CanManage(IReadOnlyCollection<EmployeeRole> actorRoles, bool actorControlsNode)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);

		return actorRoles.Contains(EmployeeRole.Administrator)
			   || actorRoles.Contains(EmployeeRole.JobManager)
			   || (actorRoles.Contains(EmployeeRole.Worker) && actorControlsNode);
	}

	/// <summary>
	///     An actor may view a work-session list if they hold any of the six baseline employee roles —
	///     recorded work is job data, and spec §7.3 grants every employee role, the
	///     <see cref="EmployeeRole.Worker" /> included, an unqualified "view employees and job data"
	///     baseline (the Worker row's restrictions are all on *managing*). Whose sessions they are is
	///     therefore irrelevant, which is why this takes no "is it their own" input: seeing another
	///     worker's session never implies being able to edit it — that stays with
	///     <see cref="CanManage" />'s node-control rule. <see cref="EmployeeRole.Requester" /> is excluded,
	///     holding no operational job visibility at all (ADR 0033), as is an actor with no role.
	///     <para>
	///         This drops the own-sessions-only restriction ADR 0032 carried over verbatim when it split
	///         <see cref="CanManage" />/<see cref="CanView" /> ("preserving the exact pre-existing self-session
	///         rule") — a legacy rule that predated, and was never justified by, the ownership model. See
	///         ADR 0041.
	///     </para>
	/// </summary>
	public static bool CanView(IReadOnlyCollection<EmployeeRole> actorRoles)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);

		return actorRoles.Any(role => role is EmployeeRole.Administrator
			or EmployeeRole.JobManager
			or EmployeeRole.Worker
			or EmployeeRole.RateManager
			or EmployeeRole.CostViewer
			or EmployeeRole.Auditor);
	}
}
