namespace JobTrack.Application;

using Abstractions;
using NodaTime;

/// <summary>Input to <see cref="ITokenCommands.IssueAsync" />.</summary>
public sealed record IssuePersonalAccessTokenRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The user the token authenticates as — always the actor themselves (ADR 0029).</summary>
	public required AppUserId TargetUserId { get; init; }

	/// <summary>A caller-chosen label to help the owner recognise this token later.</summary>
	public required string Label { get; init; }

	/// <summary>
	///     The instant this token stops being valid. Supply this for callers which already own an
	///     explicit expiry instant; otherwise use <see cref="Lifetime" /> so the command captures
	///     one authoritative current instant for both timestamps.
	/// </summary>
	public Instant? ExpiresAt { get; init; }

	/// <summary>The requested lifetime, resolved from the command's single captured current instant.</summary>
	public Duration? Lifetime { get; init; }
}
