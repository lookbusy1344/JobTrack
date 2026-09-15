namespace JobTrack.Application;

/// <summary>
///     A WebAuthn credential the framework has already verified during attestation, in a
///     framework-neutral shape (ADR 0071 §7.1). Binary members are <see cref="ReadOnlyMemory{T}" /> at
///     the boundary; the persistence port copies them before storing, so no caller retains a mutable
///     reference to persisted key material. No ASP.NET Identity type crosses into
///     <c>JobTrack.Application</c>.
/// </summary>
public sealed class VerifiedPasskey
{
	/// <summary>The WebAuthn credential ID; globally unique.</summary>
	public required ReadOnlyMemory<byte> CredentialId { get; init; }

	/// <summary>The COSE-encoded public key.</summary>
	public required ReadOnlyMemory<byte> PublicKey { get; init; }

	/// <summary>The authenticator's signature counter (unsigned 32-bit logical range).</summary>
	public long SignCount { get; init; }

	/// <summary>The canonical framework transport strings, or null when the authenticator reported none.</summary>
	public string? Transports { get; init; }

	/// <summary>Whether the authenticator performed local user verification. Enrolment requires this.</summary>
	public bool IsUserVerified { get; init; }

	/// <summary>Whether the credential is eligible for backup (sync).</summary>
	public bool IsBackupEligible { get; init; }

	/// <summary>Whether the credential is currently backed up (synced).</summary>
	public bool IsBackedUp { get; init; }

	/// <summary>The attestation object retained for the framework credential record.</summary>
	public required ReadOnlyMemory<byte> AttestationObject { get; init; }

	/// <summary>The client data JSON retained for the framework credential record.</summary>
	public required ReadOnlyMemory<byte> ClientDataJson { get; init; }
}
