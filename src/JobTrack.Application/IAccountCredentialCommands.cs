namespace JobTrack.Application;

using Abstractions;

/// <summary>Credential-sensitive account state transitions.</summary>
public interface IAccountCredentialCommands
{
	/// <summary>Enables or disables self-service two-factor authentication atomically with audit and PAT revocation.</summary>
	Task<SetTwoFactorStateResult> SetTwoFactorStateAsync(
		SetTwoFactorStateRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Atomic composite (remediation plan §2.2): verifies <see cref="ChangeOwnPasswordRequest.CurrentPassword" />
	///     against the stored hash, then updates the password hash, clears <c>RequiresPasswordChange</c>,
	///     rotates the security/concurrency stamps, revokes every personal access token, and writes the
	///     audit event, in one provider transaction -- rather than the web host coordinating those as
	///     independent mutations. Login-success/failure telemetry is deliberately not part of this
	///     transaction; callers record it separately, as attempt telemetry rather than a credential
	///     mutation.
	/// </summary>
	/// <exception cref="EntityNotFoundException">The identity user does not exist.</exception>
	/// <exception cref="AuthorizationDeniedException">
	///     <see cref="ChangeOwnPasswordRequest.ActorUserId" /> does not own <see cref="ChangeOwnPasswordRequest.IdentityUserId" />.
	/// </exception>
	/// <exception cref="InvariantViolationException">
	///     <see cref="ChangeOwnPasswordRequest.CurrentPassword" /> does not match the stored hash
	///     (<c>ConstraintId</c> <c>"account-current-password-incorrect"</c>), or
	///     <see cref="ChangeOwnPasswordRequest.NewPassword" /> does not satisfy
	///     <see cref="PasswordPolicy" /> (<c>ConstraintId</c> <c>"account-new-password-policy"</c>).
	/// </exception>
	Task<ChangeOwnPasswordResult> ChangeOwnPasswordAsync(
		ChangeOwnPasswordRequest request, CancellationToken cancellationToken = default);
}
