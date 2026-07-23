namespace JobTrack.Application;

using Abstractions;
using Ports;

/// <summary>Application command surface for credential-sensitive account state transitions.</summary>
internal sealed class AccountCredentialCommands : IAccountCredentialCommands
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

	/// <inheritdoc />
	public Task<ChangeOwnPasswordResult> ChangeOwnPasswordAsync(
		ChangeOwnPasswordRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.IdentityUserId);
		if (request.ActorUserId.IsUnspecified) {
			throw new ArgumentException("Actor user id must be specified.", nameof(request));
		}

		if (!PasswordPolicy.IsSatisfiedBy(request.NewPassword)) {
			throw new InvariantViolationException(
				"account-new-password-policy",
				$"The new password must be at least {PasswordPolicy.MinimumLength} characters and contain at least one letter and one digit.");
		}

		return JobTrackOperation.TraceAsync(
			"credentials.change-own-password",
			new CommandContext { Actor = request.ActorUserId, CorrelationId = request.CorrelationId },
			null,
			() => port.ChangeOwnPasswordAsync(request, cancellationToken));
	}
}
