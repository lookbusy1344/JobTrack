namespace JobTrack.Domain.Authorization;

using Abstractions;

/// <summary>
///     Pure authorization rules for employee profile and account-state visibility (plan §7.3 step 2).
///     The library reloads authoritative roles fresh for every operation rather than trusting cached
///     claims (plan §7.5), so callers supply the actor's current roles rather than this policy fetching
///     them itself.
/// </summary>
public static class EmployeeAccessPolicy
{
	/// <summary>
	///     An actor may view an employee's profile or account state if they are viewing their own
	///     record, or hold <see cref="EmployeeRole.Administrator" /> (spec §7.1: "Manage employee
	///     accounts, account state...").
	/// </summary>
	public static bool CanViewEmployee(
		AppUserId actorId, AppUserId targetUserId, IReadOnlyCollection<EmployeeRole> actorRoles)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);

		return actorId == targetUserId || actorRoles.Contains(EmployeeRole.Administrator);
	}

	/// <summary>
	///     An actor may assign or revoke another employee's role membership only if they hold
	///     <see cref="EmployeeRole.Administrator" /> (spec §7.1: "Only administrators may edit employee
	///     accounts or global role assignments").
	/// </summary>
	public static bool CanManageRoles(IReadOnlyCollection<EmployeeRole> actorRoles)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);

		return actorRoles.Contains(EmployeeRole.Administrator);
	}

	/// <summary>
	///     An actor may enable, disable, or reset the credential of another employee's account only if
	///     they hold <see cref="EmployeeRole.Administrator" /> (spec §7.1: "Only administrators may edit
	///     employee accounts or global role assignments"). Named separately from
	///     <see cref="CanManageRoles" /> even though currently identical: account state and role
	///     assignment are distinct authorities that happen to be granted to the same role today.
	/// </summary>
	public static bool CanManageAccounts(IReadOnlyCollection<EmployeeRole> actorRoles)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);

		return actorRoles.Contains(EmployeeRole.Administrator);
	}
}
