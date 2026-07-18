namespace JobTrack.Domain.Authorization;

using Abstractions;
using NodaTime;

/// <summary>
///     Pure lifetime rules for personal access tokens (ADR 0029): every token requires an explicit,
///     bounded expiry at issuance — there is no non-expiring token.
/// </summary>
public static class PersonalAccessTokenPolicy
{
	/// <summary>
	///     The longest lifetime a token may be issued with, forcing periodic re-issuance rather than a
	///     token living unattended indefinitely (ADR 0029).
	/// </summary>
	public static readonly Duration MaxLifetime = Duration.FromDays(365);

	/// <summary>
	///     Validates a requested expiry against <paramref name="now" /> and <see cref="MaxLifetime" />.
	/// </summary>
	/// <exception cref="InvariantViolationException">
	///     <paramref name="expiresAt" /> is not after <paramref name="now" /> (<c>ConstraintId</c>
	///     <c>"personal-access-token-expiry-not-in-future"</c>), or exceeds <see cref="MaxLifetime" />
	///     from <paramref name="now" /> (<c>ConstraintId</c> <c>"personal-access-token-expiry-too-long"</c>).
	/// </exception>
	public static void EnsureValidExpiry(Instant now, Instant expiresAt)
	{
		if (expiresAt <= now) {
			throw new InvariantViolationException(
				"personal-access-token-expiry-not-in-future", "A personal access token's expiry must be in the future.");
		}

		if (expiresAt - now > MaxLifetime) {
			throw new InvariantViolationException(
				"personal-access-token-expiry-too-long",
				$"A personal access token's lifetime may not exceed {MaxLifetime.Days} days.");
		}
	}
}
