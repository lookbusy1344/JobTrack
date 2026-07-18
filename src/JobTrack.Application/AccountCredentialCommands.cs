namespace JobTrack.Application;

using Ports;

/// <summary>Application command surface for credential-sensitive account state transitions.</summary>
public sealed class AccountCredentialCommands : IAccountCredentialCommands
{
	private readonly IAccountCredentialPort port;

	/// <summary>Creates the command surface over the persistence port.</summary>
	public AccountCredentialCommands(IAccountCredentialPort port)
	{
		ArgumentNullException.ThrowIfNull(port);

		this.port = port;
	}

	/// <inheritdoc />
	public Task<SetTwoFactorStateResult> SetTwoFactorStateAsync(
		SetTwoFactorStateRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.IdentityUserId);
		if (request.ActorUserId.IsUnspecified) {
			throw new ArgumentException("Actor user id must be specified.", nameof(request));
		}

		return JobTrackOperation.TraceAsync(
			"credentials.set-two-factor-state",
			new CommandContext { Actor = request.ActorUserId, CorrelationId = request.CorrelationId },
			null,
			() => port.SetTwoFactorStateAsync(request, cancellationToken));
	}
}
