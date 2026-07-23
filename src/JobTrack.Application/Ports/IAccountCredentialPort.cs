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
}
