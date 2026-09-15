namespace JobTrack.Application;

using Abstractions;

/// <summary>Input to <see cref="IEmployeeCommands.ResetPasskeysAsync" />.</summary>
public sealed record ResetEmployeePasskeysRequest
{
	/// <summary>The acting administrator and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The employee whose passkeys are being removed.</summary>
	public required AppUserId TargetUserId { get; init; }
}
