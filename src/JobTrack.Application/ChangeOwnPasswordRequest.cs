namespace JobTrack.Application;

using Abstractions;

/// <summary>Input for self-service password change (remediation plan §2.2).</summary>
public sealed class ChangeOwnPasswordRequest
{
	/// <summary>The signed-in account changing its own password.</summary>
	public required AppUserId ActorUserId { get; init; }

	/// <summary>The signed-in account's <c>identity_user</c> row.</summary>
	public long IdentityUserId { get; init; }

	/// <summary>
	///     The signed-in account's sign-in username -- checked against <see cref="PasswordBlocklist" />
	///     so an account cannot set its own username as its password (remediation plan §2.1).
	/// </summary>
	public required string Username { get; init; }

	/// <summary>The account's current password, verified against the stored hash before anything is changed.</summary>
	public required string CurrentPassword { get; init; }

	/// <summary>The new password to store.</summary>
	public required string NewPassword { get; init; }

	/// <summary>Correlates the credential transition with audit events and logs.</summary>
	public required Guid CorrelationId { get; init; }
}
