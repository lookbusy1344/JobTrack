namespace JobTrack.Application;

using Abstractions;

/// <summary>Input for removing one owned passkey atomically with stamp rotation, revocation, and audit.</summary>
public sealed class RemovePasskeyRequest
{
	/// <summary>The signed-in account that owns the credential.</summary>
	public required AppUserId ActorUserId { get; init; }

	/// <summary>The signed-in account's <c>identity_user</c> row.</summary>
	public long IdentityUserId { get; init; }

	/// <summary>The opaque (base64url) identifier of the credential to remove.</summary>
	public required string CredentialId { get; init; }

	/// <summary>Correlates the credential transition with audit events and logs.</summary>
	public required Guid CorrelationId { get; init; }
}
