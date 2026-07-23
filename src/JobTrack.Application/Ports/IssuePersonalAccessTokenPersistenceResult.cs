namespace JobTrack.Application.Ports;

using Abstractions;
using NodaTime;

/// <summary>
///     Persisted state returned by <see cref="IPersonalAccessTokenPort.IssueAsync" /> — see
///     <see cref="IssuePersonalAccessTokenPersistenceRequest" />.
/// </summary>
internal sealed record IssuePersonalAccessTokenPersistenceResult
{
	/// <summary>The new token's identifier.</summary>
	public required PersonalAccessTokenId Id { get; init; }

	/// <summary>The label supplied at issuance.</summary>
	public required string Label { get; init; }

	/// <summary>The instant this token was issued.</summary>
	public required Instant CreatedAt { get; init; }

	/// <summary>The instant this token stops being valid.</summary>
	public required Instant ExpiresAt { get; init; }
}
