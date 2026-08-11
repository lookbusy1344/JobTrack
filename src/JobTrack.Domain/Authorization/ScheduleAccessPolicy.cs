namespace JobTrack.Domain.Authorization;

using Abstractions;

/// <summary>
///     Pure authorization rules for schedule commands (plan §7.3 step 8; spec §7.3: "Workers may edit
///     their own schedules and exceptions, but not another employee's"). Whether the schedule being
///     changed is the actor's own is a persistence fact supplied by the caller, the same fresh-reload
///     rule as the other policies in this namespace.
/// </summary>
public static class ScheduleAccessPolicy
{
	/// <summary>
	///     An actor may manage a schedule version or exception if they hold
	///     <see cref="EmployeeRole.Administrator" />, or the schedule is their own.
	/// </summary>
	public static bool CanManage(IReadOnlyCollection<EmployeeRole> actorRoles, bool isOwnSchedule)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);

		return actorRoles.Contains(EmployeeRole.Administrator) || isOwnSchedule;
	}
}
