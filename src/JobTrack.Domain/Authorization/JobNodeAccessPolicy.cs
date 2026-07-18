namespace JobTrack.Domain.Authorization;

using Abstractions;

/// <summary>
///     Pure authorization rules for job-node structural commands (plan §7.3 step 3; spec §7.3: "Job
///     manager: manage the complete job hierarchy... Worker: manage jobs they own and their owned
///     subtrees"). Ownership is a tree fact only persistence can compute (walking <c>parent_id</c>), so
///     callers supply it rather than this policy fetching it itself — the same fresh-reload rule as
///     <see cref="EmployeeAccessPolicy" />.
/// </summary>
public static class JobNodeAccessPolicy
{
	/// <summary>
	///     An actor may manage a job node if they hold <see cref="EmployeeRole.Administrator" /> or
	///     <see cref="EmployeeRole.JobManager" />, or hold <see cref="EmployeeRole.Worker" /> and own the
	///     node itself or one of its ancestors.
	/// </summary>
	public static bool CanManage(IReadOnlyCollection<EmployeeRole> actorRoles, bool actorOwnsNodeOrAncestor)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);

		return actorRoles.Contains(EmployeeRole.Administrator)
			   || actorRoles.Contains(EmployeeRole.JobManager)
			   || (actorRoles.Contains(EmployeeRole.Worker) && actorOwnsNodeOrAncestor);
	}
}
