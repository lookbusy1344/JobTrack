namespace JobTrack.Domain.Authorization;

using Abstractions;

/// <summary>
///     Pure authorization rule for configuring departments and request holding areas (ADR 0033; plan
///     §8, admin configuration page). This is a structural/administrative concern distinct from
///     <see cref="RequesterAccessPolicy" />, which governs the requester's own submit/view/comment
///     actions, not who may define where those requests land.
/// </summary>
public static class RequestHoldingAreaConfigurationPolicy
{
	/// <summary>
	///     An actor may create, edit, or deactivate a department or request holding area if they hold
	///     <see cref="EmployeeRole.Administrator" /> or <see cref="EmployeeRole.JobManager" />.
	/// </summary>
	public static bool CanConfigure(IReadOnlyCollection<EmployeeRole> actorRoles)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);

		return actorRoles.Contains(EmployeeRole.Administrator) || actorRoles.Contains(EmployeeRole.JobManager);
	}
}
