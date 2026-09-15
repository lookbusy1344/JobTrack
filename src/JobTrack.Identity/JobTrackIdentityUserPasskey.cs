namespace JobTrack.Identity;

using NodaTime;

/// <summary>
///     Persistence shape of the <c>identity_user_passkey</c> table (schema version 0027, ADR 0071) as
///     mapped by <see cref="JobTrackIdentityDbContext" /> — the ongoing-authentication mapping backing
///     <c>IUserPasskeyStore&lt;JobTrackIdentityUser&gt;</c>, independent of the library's atomic
///     enrol/remove/reset mapping (ADR 0022). A contract test prevents the two mappings drifting.
/// </summary>
public sealed class JobTrackIdentityUserPasskey
{
	public required byte[] CredentialId { get; set; }

	public long IdentityUserId { get; set; }

	public required string Name { get; set; }

	/// <summary>Invariant <c>ToUpperInvariant</c> of <see cref="Name" />; unique per account. The database compares this stored value exactly.</summary>
	public required string NormalizedName { get; set; }

	public required byte[] PublicKey { get; set; }

	public Instant CreatedAt { get; set; }

	/// <summary>Unsigned 32-bit logical range; never lowered below its persisted value during an assertion update.</summary>
	public long SignCount { get; set; }

	/// <summary>Canonical framework transport strings serialized once (<see cref="Abstractions.PasskeyTransports" />), or null when none.</summary>
	public string? Transports { get; set; }

	public bool IsUserVerified { get; set; }

	public bool IsBackupEligible { get; set; }

	public bool IsBackedUp { get; set; }

	public required byte[] AttestationObject { get; set; }

	public required byte[] ClientDataJson { get; set; }

	public long RowVersion { get; set; }
}
