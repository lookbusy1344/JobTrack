namespace JobTrack.Identity;

using Abstractions;

/// <summary>
///     Persistence shape of the <c>identity_user</c> table (schema version 0002) as mapped by
///     <see cref="JobTrackIdentityDbContext" /> — the ongoing-authentication mapping (ADR 0022),
///     independent of the library's bootstrap-only <c>IdentityUserEntity</c> mapping.
/// </summary>
public sealed class JobTrackIdentityUser
{
	public long Id { get; set; }

	public required AppUserId AppUserId { get; set; }

	public required string UserName { get; set; }

	public required string NormalizedUserName { get; set; }

	public required string PasswordHash { get; set; }

	public required string SecurityStamp { get; set; }

	public required string ConcurrencyStamp { get; set; }

	public bool RequiresPasswordChange { get; set; } = true;

	public bool IsEnabled { get; set; } = true;

	public bool LockoutEnabled { get; set; } = true;

	public DateTimeOffset? LockoutEnd { get; set; }

	public int AccessFailedCount { get; set; }

	/// <summary>Optional TOTP two-factor authentication (ADR 0037).</summary>
	public bool TwoFactorEnabled { get; set; }

	/// <summary>
	///     The TOTP shared secret, encrypted at rest via ASP.NET Core Data Protection --
	///     never plaintext. Non-null while an authenticator key exists, whether or not
	///     <see cref="TwoFactorEnabled" /> has been confirmed yet.
	/// </summary>
	public byte[]? AuthenticatorKeyProtected { get; set; }

	public DateTimeOffset? TwoFactorEnabledAt { get; set; }
}
