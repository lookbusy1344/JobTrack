namespace JobTrack.Application;

using Abstractions;

/// <summary>Input to <see cref="IJobQueries.GetAccountStateAsync" />.</summary>
public sealed record GetAccountStateRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The employee whose account state is requested.</summary>
	public required AppUserId TargetUserId { get; init; }
}
