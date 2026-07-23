namespace JobTrack.Application.Ports;

using Abstractions;
using NodaTime;

/// <summary>Input to <see cref="IPersonalAccessTokenPort.IssueAsync" /> — carries an already-computed hash, never the plaintext.</summary>
internal sealed record IssuePersonalAccessTokenPersistenceRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The user this token authenticates as.</summary>
	public required AppUserId TargetUserId { get; init; }

	/// <summary>The label supplied at issuance.</summary>
	public required string Label { get; init; }

	/// <summary>The already-computed hash of the plaintext token — never the plaintext itself.</summary>
	public required string TokenHash { get; init; }

	/// <summary>The instant this token is issued.</summary>
	public required Instant CreatedAt { get; init; }

	/// <summary>The instant this token stops being valid.</summary>
	public required Instant ExpiresAt { get; init; }
}
