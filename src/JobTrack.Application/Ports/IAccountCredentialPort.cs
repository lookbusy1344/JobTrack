namespace JobTrack.Application.Ports;

/// <summary>Persistence port for credential-sensitive account state transitions.</summary>
internal interface IAccountCredentialPort
{
	/// <inheritdoc cref="IAccountCredentialCommands.SetTwoFactorStateAsync" />
	Task<SetTwoFactorStateResult> SetTwoFactorStateAsync(
		SetTwoFactorStateRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IAccountCredentialCommands.ChangeOwnPasswordAsync" />
	Task<ChangeOwnPasswordResult> ChangeOwnPasswordAsync(
		ChangeOwnPasswordRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IAccountCredentialCommands.EnsurePasskeyUserHandleAsync" />
	Task<EnsurePasskeyUserHandleResult> EnsurePasskeyUserHandleAsync(
		EnsurePasskeyUserHandleRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IAccountCredentialCommands.AddPasskeyAsync" />
	Task<AddPasskeyResult> AddPasskeyAsync(
		AddPasskeyRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IAccountCredentialCommands.ListPasskeysAsync" />
	Task<IReadOnlyList<PasskeySummary>> ListPasskeysAsync(
		ListPasskeysRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IAccountCredentialCommands.RenamePasskeyAsync" />
	Task<RenamePasskeyResult> RenamePasskeyAsync(
		RenamePasskeyRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="IAccountCredentialCommands.RemovePasskeyAsync" />
	Task<RemovePasskeyResult> RemovePasskeyAsync(
		RemovePasskeyRequest request, CancellationToken cancellationToken = default);
}
