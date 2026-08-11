namespace JobTrack.Domain.Authorization;

using Abstractions;

/// <summary>
///     Pure authorization rules for cost queries (plan §7.3 step 10; spec §7.3 role table: "Cost viewer
///     | Additive permission to view rates, rate provenance, and calculated costs"). Ordinary job/employee
///     visibility never implies cost visibility, and <see cref="EmployeeRole.RateManager" /> alone does not
///     grant it either — spec §7.3 states rate management is granted "without granting ... cost
///     visibility" (see <see cref="RateAccessPolicy" />). ADR 0040 adds an ownership carve-out, matching
///     every other node-scoped policy here (<see cref="JobNodeAccessPolicy" />,
///     <see cref="WorkSessionAccessPolicy" />, <see cref="ScheduleAccessPolicy" />).
/// </summary>
public static class CostAccessPolicy
{
	/// <summary>
	///     An actor may view cost details and hierarchy totals if they hold
	///     <see cref="EmployeeRole.Administrator" /> or <see cref="EmployeeRole.CostViewer" />, or own the
	///     queried node or one of its ancestors (ADR 0040).
	/// </summary>
	public static bool CanView(IReadOnlyCollection<EmployeeRole> actorRoles, bool ownsNodeOrAncestor)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);

		return actorRoles.Contains(EmployeeRole.Administrator) || actorRoles.Contains(EmployeeRole.CostViewer) || ownsNodeOrAncestor;
	}

	/// <summary>
	///     Whether one node's <em>individual</em> cost may be shown, once <see cref="CanView" /> has already
	///     admitted the actor to the surrounding subtree (ADR 0042). <see cref="CanView" />'s ownership
	///     carve-out is deliberately coarse — owning a node admits its whole subtree — so without this
	///     second, per-node test an owner would read every descendant leaf's individual cost, including
	///     leaves owned by other people. A leaf's cost alongside its visible session hours is enough to
	///     infer that worker's hourly rate, which spec §7.3 reserves to the rate/cost roles.
	///     <para>
	///         A branch's cost is an aggregate over its descendants, so it stays visible: no individual's rate
	///         is recoverable from a roll-up. An individual leaf's cost is shown only where no worker's rate is
	///         exposed by it — the actor's own leaf, or an unassigned one (nobody's rate to infer) — unless the
	///         actor holds <see cref="EmployeeRole.Administrator" />/<see cref="EmployeeRole.CostViewer" />, for
	///         whom cost visibility is the whole point of the role.
	///     </para>
	/// </summary>
	/// <param name="actorRoles">The actor's freshly reloaded roles.</param>
	/// <param name="nodeHasChildren">Whether the node is a branch (an aggregate) rather than a leaf.</param>
	/// <param name="nodeOwnerUserId">The node's direct owner, or <see langword="null" /> when unassigned.</param>
	/// <param name="actorId">The acting user.</param>
	public static bool CanViewNodeCost(
		IReadOnlyCollection<EmployeeRole> actorRoles, bool nodeHasChildren, AppUserId? nodeOwnerUserId, AppUserId actorId)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);

		return actorRoles.Contains(EmployeeRole.Administrator)
			   || actorRoles.Contains(EmployeeRole.CostViewer)
			   || nodeHasChildren
			   || nodeOwnerUserId is null
			   || nodeOwnerUserId == actorId;
	}
}
