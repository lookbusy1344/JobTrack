namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Result of <see cref="IEmployeeCommands.AssignRoleAsync" /> and
///     <see cref="IEmployeeCommands.RevokeRoleAsync" />: the employee's complete current role
///     membership after the operation, not just the single role acted on.
/// </summary>
public sealed record EmployeeRolesResult
{
	/// <summary>The employee whose role membership changed.</summary>
	public required AppUserId UserId { get; init; }

	/// <summary>The employee's complete current role membership.</summary>
	public required EquatableArray<EmployeeRole> Roles { get; init; }
}
