namespace JobTrack.Persistence.Shared.Entities;

using NodaTime;

/// <summary>
///     Persistence shape of the <c>identity_user_passkey</c> table (schema version 0027, ADR 0071) —
///     one enrolled WebAuthn credential, on the credential boundary. Mapped independently here for the
///     atomic enrol/remove/rename/reset transitions and, separately, in <c>JobTrack.Identity</c> for
///     <c>IUserPasskeyStore</c> (ADR 0022); a contract test prevents the two mappings drifting.
/// </summary>
internal sealed class IdentityUserPasskeyEntity
{
	public required byte[] CredentialId { get; set; }

	public long IdentityUserId { get; set; }

	public required string Name { get; set; }

	/// <summary>Invariant <c>ToUpperInvariant</c> of <see cref="Name" />; unique per account. The database compares this stored value exactly.</summary>
	public required string NormalizedName { get; set; }

	public required byte[] PublicKey { get; set; }

	public Instant CreatedAt { get; set; }

	/// <summary>Unsigned 32-bit logical range; never lowered below a non-zero value.</summary>
	public long SignCount { get; set; }

	public string? Transports { get; set; }

	public bool IsUserVerified { get; set; }

	public bool IsBackupEligible { get; set; }

	public bool IsBackedUp { get; set; }

	public required byte[] AttestationObject { get; set; }

	public required byte[] ClientDataJson { get; set; }

	public long RowVersion { get; set; }
}
