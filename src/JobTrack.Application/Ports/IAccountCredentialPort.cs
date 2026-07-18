namespace JobTrack.Application.Ports;

/// <summary>Persistence port for credential-sensitive account state transitions.</summary>
public interface IAccountCredentialPort
{
	/// <inheritdoc cref="IAccountCredentialCommands.SetTwoFactorStateAsync" />
	Task<SetTwoFactorStateResult> SetTwoFactorStateAsync(
		SetTwoFactorStateRequest request, CancellationToken cancellationToken = default);
}
