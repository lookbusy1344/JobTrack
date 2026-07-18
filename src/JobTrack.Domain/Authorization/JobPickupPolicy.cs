namespace JobTrack.Domain.Authorization;

using Abstractions;

/// <summary>
///     Pure authorization rule for claiming an unassigned node from the pickup pool (ownership model
///     §4.3; ADR 0031). Ancestors are irrelevant to pickup — a node with a <see langword="null" /> owner
///     is always claimable by any <see cref="EmployeeRole.Worker" />, <see cref="EmployeeRole.JobManager" />,
///     or <see cref="EmployeeRole.Administrator" />, regardless of who controls an ancestor. Whether the
///     node is currently unassigned is a persistence fact supplied by the caller, the same fresh-reload
///     rule as <see cref="EmployeeAccessPolicy" />/<see cref="JobNodeAccessPolicy" />.
/// </summary>
public static class JobPickupPolicy
{
	/// <summary>
	///     An actor may pick up a node if it is currently unassigned (<paramref name="ownerIsNull" />)
	///     and they hold <see cref="EmployeeRole.Worker" />, <see cref="EmployeeRole.JobManager" />, or
	///     <see cref="EmployeeRole.Administrator" />. Read-only roles (RateManager, CostViewer, Auditor)
	///     may never pick up, since they cannot work.
	/// </summary>
	public static bool CanPickUp(IReadOnlyCollection<EmployeeRole> actorRoles, bool ownerIsNull)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);

		return ownerIsNull
			   && (actorRoles.Contains(EmployeeRole.Worker)
				   || actorRoles.Contains(EmployeeRole.JobManager)
				   || actorRoles.Contains(EmployeeRole.Administrator));
	}
}
