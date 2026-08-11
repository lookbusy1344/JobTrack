namespace JobTrack.Persistence.Shared.Entities;

using Abstractions;
using NodaTime;

/// <summary>
///     Persistence shape of the <c>personal_access_token</c> table (ADR 0029). Only a salted hash of
///     the token is ever stored — the plaintext exists solely in the issuance result returned once at
///     creation.
/// </summary>
internal sealed class PersonalAccessTokenEntity
{
	public required PersonalAccessTokenId Id { get; set; }

	public required AppUserId AppUserId { get; set; }

	public required string TokenHash { get; set; }

	public required string Label { get; set; }

	public Instant CreatedAt { get; set; }

	public Instant ExpiresAt { get; set; }

	public Instant? RevokedAt { get; set; }

	public Instant? LastUsedAt { get; set; }
}
