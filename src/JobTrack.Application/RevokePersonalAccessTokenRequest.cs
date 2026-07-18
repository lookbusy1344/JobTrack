namespace JobTrack.Application;

using Abstractions;

/// <summary>Input to <see cref="ITokenCommands.RevokeAsync" />.</summary>
public sealed record RevokePersonalAccessTokenRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The owner of the token being revoked.</summary>
	public required AppUserId TargetUserId { get; init; }

	/// <summary>The token to revoke.</summary>
	public required PersonalAccessTokenId TokenId { get; init; }
}
