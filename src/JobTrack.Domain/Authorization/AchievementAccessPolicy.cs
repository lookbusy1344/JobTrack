namespace JobTrack.Domain.Authorization;

using Abstractions;

/// <summary>
///     Pure authorization rules for achievement changes (plan §7.3 step 7; ADR 0001). Ordinary forward
///     transitions follow the same subtree-ownership rule as every other job-node command
///     (<see cref="JobNodeAccessPolicy" />), but reopening a terminal state back to
///     <see cref="Achievement.Waiting" /> requires <see cref="EmployeeRole.Administrator" /> or
///     <see cref="EmployeeRole.JobManager" /> regardless of ownership (ADR 0001: "Reopening authority").
/// </summary>
public static class AchievementAccessPolicy
{
	/// <summary>Whether the actor may make this achievement change.</summary>
	public static bool CanSetAchievement(
		IReadOnlyCollection<EmployeeRole> actorRoles, bool actorOwnsNodeOrAncestor, bool isReopening)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);

		if (isReopening) {
			return actorRoles.Contains(EmployeeRole.Administrator) || actorRoles.Contains(EmployeeRole.JobManager);
		}

		return JobNodeAccessPolicy.CanManage(actorRoles, actorOwnsNodeOrAncestor);
	}
}
