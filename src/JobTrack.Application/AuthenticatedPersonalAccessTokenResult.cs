namespace JobTrack.Application;

using Abstractions;

/// <summary>Result of a successful <see cref="ITokenCommands.TryAuthenticateAsync" /> call.</summary>
public sealed record AuthenticatedPersonalAccessTokenResult
{
	/// <summary>The user this token authenticates as.</summary>
	public required AppUserId UserId { get; init; }

	/// <summary>The token's identifier.</summary>
	public required PersonalAccessTokenId TokenId { get; init; }
}
