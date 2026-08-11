namespace JobTrack.Application;

using Abstractions;

/// <summary>Input to <see cref="IEmployeeCommands.AssignRoleAsync" />.</summary>
public sealed record AssignEmployeeRoleRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The employee to grant the role to.</summary>
	public required AppUserId TargetUserId { get; init; }

	/// <summary>The role being granted.</summary>
	public required EmployeeRole Role { get; init; }
}
