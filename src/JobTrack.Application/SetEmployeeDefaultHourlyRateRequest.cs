namespace JobTrack.Application;

using Abstractions;

/// <summary>Input to <see cref="IEmployeeCommands.SetDefaultHourlyRateAsync" />.</summary>
public sealed record SetEmployeeDefaultHourlyRateRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The employee whose default hourly rate is being set.</summary>
	public required AppUserId TargetUserId { get; init; }

	/// <summary>The default hourly rate to apply before any effective-dated rate overrides.</summary>
	public required HourlyRate DefaultHourlyRate { get; init; }
}
