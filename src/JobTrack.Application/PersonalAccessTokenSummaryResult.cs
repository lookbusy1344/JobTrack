namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>
///     One row of <see cref="ITokenCommands.ListAsync" />'s result. Never carries the token hash or
///     plaintext (spec §16) — only enough for an owner to recognise and decide whether to revoke it.
/// </summary>
public sealed record PersonalAccessTokenSummaryResult
{
	/// <summary>The token's identifier.</summary>
	public required PersonalAccessTokenId Id { get; init; }

	/// <summary>The label supplied at issuance.</summary>
	public required string Label { get; init; }

	/// <summary>The instant this token was issued.</summary>
	public required Instant CreatedAt { get; init; }

	/// <summary>The instant this token stops being valid.</summary>
	public required Instant ExpiresAt { get; init; }

	/// <summary>The instant this token was revoked, if it has been.</summary>
	public Instant? RevokedAt { get; init; }

	/// <summary>The instant this token was last used to authenticate, if ever.</summary>
	public Instant? LastUsedAt { get; init; }
}
