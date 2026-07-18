namespace JobTrack.Application;

using Abstractions;

/// <summary>Input to <see cref="IJobQueries.GetEmployeeProfileAsync" />.</summary>
public sealed record GetEmployeeProfileRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The employee whose profile is requested.</summary>
	public required AppUserId TargetUserId { get; init; }
}
