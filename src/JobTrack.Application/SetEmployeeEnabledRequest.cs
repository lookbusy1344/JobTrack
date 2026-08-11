namespace JobTrack.Application;

using Abstractions;

/// <summary>Input to <see cref="IEmployeeCommands.SetEnabledAsync" />.</summary>
public sealed record SetEmployeeEnabledRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The employee whose account is being enabled or disabled.</summary>
	public required AppUserId TargetUserId { get; init; }

	/// <summary>Whether the account should be able to sign in.</summary>
	public required bool Enabled { get; init; }
}
