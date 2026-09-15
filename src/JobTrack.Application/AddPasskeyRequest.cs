namespace JobTrack.Application;

using Abstractions;

/// <summary>Input for enrolling a verified passkey under the actor's account (ADR 0071 §7.1).</summary>
public sealed class AddPasskeyRequest
{
	/// <summary>The signed-in account enrolling the credential.</summary>
	public required AppUserId ActorUserId { get; init; }

	/// <summary>The signed-in account's <c>identity_user</c> row.</summary>
	public long IdentityUserId { get; init; }

	/// <summary>The required memorable friendly name (1–100 code points; <see cref="PasskeyPolicy" />).</summary>
	public required string Name { get; init; }

	/// <summary>The credential the framework verified during attestation.</summary>
	public required VerifiedPasskey Credential { get; init; }

	/// <summary>Correlates the credential transition with audit events and logs.</summary>
	public required Guid CorrelationId { get; init; }
}
