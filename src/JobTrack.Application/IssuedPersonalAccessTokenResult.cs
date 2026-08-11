namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     Result of <see cref="ITokenCommands.IssueAsync" />. <see cref="Token" /> is the plaintext bearer
///     credential, shown exactly once — it is never persisted or retrievable again (ADR 0029).
/// </summary>
public sealed record IssuedPersonalAccessTokenResult
{
	/// <summary>The token's identifier, usable to revoke it later without the plaintext.</summary>
	public required PersonalAccessTokenId Id { get; init; }

	/// <summary>The one-time-visible plaintext bearer credential.</summary>
	public required string Token { get; init; }

	/// <summary>The label supplied at issuance.</summary>
	public required string Label { get; init; }

	/// <summary>The instant this token was issued.</summary>
	public required Instant CreatedAt { get; init; }

	/// <summary>The instant this token stops being valid.</summary>
	public required Instant ExpiresAt { get; init; }
}
