namespace JobTrack.Application;

/// <summary>
///     A display-safe summary of one enrolled passkey (ADR 0071 §8.1). Carries no credential ID bytes,
///     AAGUID, public key, attestation, client data, counter, or transports — only the opaque
///     identifier needed to address it, its name, when it was created, and the backup flags that drive
///     a cautious synced/device-bound hint.
/// </summary>
public sealed class PasskeySummary
{
	/// <summary>The opaque (base64url) identifier used to rename or remove this credential.</summary>
	public required string CredentialId { get; init; }

	/// <summary>The friendly name the employee gave the credential.</summary>
	public required string Name { get; init; }

	/// <summary>When the credential was enrolled, as a public-boundary value.</summary>
	public required DateTimeOffset CreatedAt { get; init; }

	/// <summary>Whether the credential is eligible for backup (sync).</summary>
	public bool IsBackupEligible { get; init; }

	/// <summary>Whether the credential is currently backed up (synced).</summary>
	public bool IsBackedUp { get; init; }
}
