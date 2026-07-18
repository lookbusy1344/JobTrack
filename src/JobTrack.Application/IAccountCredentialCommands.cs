namespace JobTrack.Application;

/// <summary>Credential-sensitive account state transitions.</summary>
public interface IAccountCredentialCommands
{
	/// <summary>Enables or disables self-service two-factor authentication atomically with audit and PAT revocation.</summary>
	Task<SetTwoFactorStateResult> SetTwoFactorStateAsync(
		SetTwoFactorStateRequest request, CancellationToken cancellationToken = default);
}
