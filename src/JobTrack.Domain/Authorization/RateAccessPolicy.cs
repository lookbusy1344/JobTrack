namespace JobTrack.Domain.Authorization;

using Abstractions;

/// <summary>
///     Pure authorization rules for rate commands (plan §7.3 step 9; spec §7.3 role table: "Rate
///     manager | Additive permission to manage employee rates and node rate overrides without granting
///     employee-account administration or cost visibility"). Unlike <see cref="ScheduleAccessPolicy" />,
///     there is no "own rate" concept — a worker never sets their own pay rate.
/// </summary>
public static class RateAccessPolicy
{
	/// <summary>
	///     An actor may manage a user cost rate or node rate override if they hold
	///     <see cref="EmployeeRole.Administrator" /> or <see cref="EmployeeRole.RateManager" />.
	/// </summary>
	public static bool CanManage(IReadOnlyCollection<EmployeeRole> actorRoles)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);

		return actorRoles.Contains(EmployeeRole.Administrator) || actorRoles.Contains(EmployeeRole.RateManager);
	}
}
