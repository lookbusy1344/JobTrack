namespace JobTrack.Application;

using Abstractions;

/// <summary>Input for the idempotent creation of an account's stable WebAuthn user handle (ADR 0071 §5).</summary>
public sealed class EnsurePasskeyUserHandleRequest
{
	/// <summary>The signed-in account whose handle is ensured.</summary>
	public required AppUserId ActorUserId { get; init; }

	/// <summary>The signed-in account's <c>identity_user</c> row.</summary>
	public long IdentityUserId { get; init; }

	/// <summary>Correlates the operation with logs.</summary>
	public required Guid CorrelationId { get; init; }
}
