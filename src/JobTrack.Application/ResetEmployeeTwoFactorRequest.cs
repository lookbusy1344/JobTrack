namespace JobTrack.Application;

using Abstractions;

/// <summary>Input to <see cref="IEmployeeCommands.ResetTwoFactorAsync" />.</summary>
public sealed record ResetEmployeeTwoFactorRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The employee whose two-factor enrolment is being cleared.</summary>
	public required AppUserId TargetUserId { get; init; }
}
