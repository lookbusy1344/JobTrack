namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Input to <see cref="ITokenCommands.RevokeAllAsync" /> — revokes every live token for a user in
///     one call. Used at every security-sensitive account transition that already revokes web sessions
///     (disablement, password reset, password change, role change, emergency reset — ADR 0029), not
///     only from a user's own token-management action.
/// </summary>
public sealed record RevokeAllPersonalAccessTokensRequest
{
	/// <summary>The acting user and correlation identifier.</summary>
	public required CommandContext Context { get; init; }

	/// <summary>The user whose tokens are all being revoked.</summary>
	public required AppUserId TargetUserId { get; init; }
}
