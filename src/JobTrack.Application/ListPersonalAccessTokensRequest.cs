namespace JobTrack.Application;

using Abstractions;

/// <summary>Input to <see cref="ITokenCommands.ListAsync" />.</summary>
public sealed record ListPersonalAccessTokensRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The user whose tokens are being listed.</summary>
	public required AppUserId TargetUserId { get; init; }
}
