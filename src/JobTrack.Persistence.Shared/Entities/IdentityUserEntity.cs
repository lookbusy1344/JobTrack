namespace JobTrack.Persistence.Shared.Entities;

using Abstractions;
using NodaTime;

/// <summary>
///     Persistence shape of the <c>identity_user</c> table (schema version 0002) — the credential
///     row, linked 1:1 to <see cref="AppUserEntity" /> via a unique <see cref="AppUserId" /> foreign
///     key. Never carries employee-domain profile data (spec §6.1).
/// </summary>
internal sealed class IdentityUserEntity
{
	public long Id { get; set; }

	public required AppUserId AppUserId { get; set; }

	public required string UserName { get; set; }

	public required string NormalizedUserName { get; set; }

	public required string PasswordHash { get; set; }

	public required string SecurityStamp { get; set; }

	public required string ConcurrencyStamp { get; set; }

	public bool RequiresPasswordChange { get; set; }

	public bool IsEnabled { get; set; }

	public bool LockoutEnabled { get; set; }

	public Instant? LockoutEnd { get; set; }

	public int AccessFailedCount { get; set; }

	/// <summary>Optional TOTP two-factor authentication (ADR 0037).</summary>
	public bool TwoFactorEnabled { get; set; }

	/// <summary>
	///     The TOTP shared secret, encrypted at rest via ASP.NET Core Data Protection —
	///     opaque to this port, which never decrypts or reads the plaintext key.
	/// </summary>
	public byte[]? AuthenticatorKeyProtected { get; set; }

	public Instant? TwoFactorEnabledAt { get; set; }
}
