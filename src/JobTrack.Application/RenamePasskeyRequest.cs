namespace JobTrack.Application;

using Abstractions;

/// <summary>Input for renaming one owned passkey (audited metadata; revokes nothing — ADR 0071 §7).</summary>
public sealed class RenamePasskeyRequest
{
	/// <summary>The signed-in account that owns the credential.</summary>
	public required AppUserId ActorUserId { get; init; }

	/// <summary>The signed-in account's <c>identity_user</c> row.</summary>
	public long IdentityUserId { get; init; }

	/// <summary>The opaque (base64url) identifier of the credential to rename.</summary>
	public required string CredentialId { get; init; }

	/// <summary>The new friendly name (1–100 code points; unique per account under <see cref="PasskeyPolicy" />).</summary>
	public required string NewName { get; init; }

	/// <summary>Correlates the rename with audit events and logs.</summary>
	public required Guid CorrelationId { get; init; }
}
