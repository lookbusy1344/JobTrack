namespace JobTrack.Application;

using Abstractions;

/// <summary>Input to <see cref="IEmployeeCommands.RevokeRoleAsync" />.</summary>
public sealed record RevokeEmployeeRoleRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The employee to revoke the role from.</summary>
	public required AppUserId TargetUserId { get; init; }

	/// <summary>The role being revoked.</summary>
	public required EmployeeRole Role { get; init; }
}
