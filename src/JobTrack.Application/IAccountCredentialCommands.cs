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

	/// <summary>
	///     Idempotently ensures the actor's account has a stable WebAuthn user handle, generating one
	///     under the identity-user write lock only when absent, and returns it (ADR 0071 §5).
	/// </summary>
	/// <exception cref="EntityNotFoundException">The identity user does not exist.</exception>
	/// <exception cref="AuthorizationDeniedException">The actor does not own the identity user.</exception>
	Task<EnsurePasskeyUserHandleResult> EnsurePasskeyUserHandleAsync(
		EnsurePasskeyUserHandleRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Enrols a verified passkey under the actor's account, atomically inserting the credential,
	///     rotating the security/concurrency stamps, revoking every personal access token, and writing a
	///     secret-free audit event, in one transaction (ADR 0071 §7).
	/// </summary>
	/// <exception cref="EntityNotFoundException">The identity user does not exist.</exception>
	/// <exception cref="AuthorizationDeniedException">The actor does not own the identity user.</exception>
	/// <exception cref="InvariantViolationException">
	///     The name is not <see cref="PasskeyPolicy" />-acceptable (<c>ConstraintId</c>
	///     <c>"passkey-name-policy"</c>), a name already exists on the account
	///     (<c>"passkey-name-duplicate"</c>), the account is at <see cref="PasskeyPolicy.MaxPasskeysPerAccount" />
	///     (<c>"passkey-max-count"</c>), or the credential is not user-verified (<c>"passkey-not-user-verified"</c>).
	/// </exception>
	Task<AddPasskeyResult> AddPasskeyAsync(
		AddPasskeyRequest request, CancellationToken cancellationToken = default);

	/// <summary>Lists the actor's own enrolled passkeys as display-safe summaries, most recent first.</summary>
	/// <exception cref="EntityNotFoundException">The identity user does not exist.</exception>
	/// <exception cref="AuthorizationDeniedException">The actor does not own the identity user.</exception>
	Task<IReadOnlyList<PasskeySummary>> ListPasskeysAsync(
		ListPasskeysRequest request, CancellationToken cancellationToken = default);

	/// <summary>Renames one owned passkey. Audited metadata; rotates no stamps and revokes nothing (ADR 0071 §7).</summary>
	/// <exception cref="EntityNotFoundException">The credential does not exist or is not owned by the actor.</exception>
	/// <exception cref="AuthorizationDeniedException">The actor does not own the identity user.</exception>
	/// <exception cref="InvariantViolationException">
	///     The new name is not acceptable (<c>"passkey-name-policy"</c>) or already exists on the account
	///     (<c>"passkey-name-duplicate"</c>).
	/// </exception>
	Task<RenamePasskeyResult> RenamePasskeyAsync(
		RenamePasskeyRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Removes one owned passkey atomically with stamp rotation, PAT revocation, and audit (ADR 0071
	///     §7). Removing the final passkey is allowed — the password remains the recovery credential.
	/// </summary>
	/// <exception cref="EntityNotFoundException">The credential does not exist or is not owned by the actor.</exception>
	/// <exception cref="AuthorizationDeniedException">The actor does not own the identity user.</exception>
	Task<RemovePasskeyResult> RemovePasskeyAsync(
		RemovePasskeyRequest request, CancellationToken cancellationToken = default);
}
