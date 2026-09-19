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
			new CommandContext {
				Actor = request.ActorUserId,
				CorrelationId = request.CorrelationId,
			},
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

		PasswordPolicyGuard.EnsureAcceptable(request.NewPassword, request.Username);

		return JobTrackOperation.TraceAsync(
			"credentials.change-own-password",
			new CommandContext {
				Actor = request.ActorUserId,
				CorrelationId = request.CorrelationId,
			},
			null,
			() => port.ChangeOwnPasswordAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<EnsurePasskeyUserHandleResult> EnsurePasskeyUserHandleAsync(
		EnsurePasskeyUserHandleRequest request, CancellationToken cancellationToken = default)
	{
		EnsureActorAndIdentity(request, request?.ActorUserId, request?.IdentityUserId);

		return JobTrackOperation.TraceAsync(
			"credentials.ensure-passkey-user-handle",
			new CommandContext {
				Actor = request!.ActorUserId,
				CorrelationId = request.CorrelationId,
			},
			null,
			() => port.EnsurePasskeyUserHandleAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<AddPasskeyResult> AddPasskeyAsync(
		AddPasskeyRequest request, CancellationToken cancellationToken = default)
	{
		EnsureActorAndIdentity(request, request?.ActorUserId, request?.IdentityUserId);
		ArgumentNullException.ThrowIfNull(request!.Credential);
		EnsureNameAcceptable(request.Name);
		if (!request.Credential.IsUserVerified) {
			throw new InvariantViolationException(ConstraintIds.PasskeyNotUserVerified,
				"A passkey must be user-verified before it can be enrolled.");
		}

		EnsureCredentialMaterialAcceptable(request.Credential);

		return JobTrackOperation.TraceAsync(
			"credentials.add-passkey",
			new CommandContext {
				Actor = request.ActorUserId,
				CorrelationId = request.CorrelationId,
			},
			null,
			() => port.AddPasskeyAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<IReadOnlyList<PasskeySummary>> ListPasskeysAsync(
		ListPasskeysRequest request, CancellationToken cancellationToken = default)
	{
		EnsureActorAndIdentity(request, request?.ActorUserId, request?.IdentityUserId);

		return port.ListPasskeysAsync(request!, cancellationToken);
	}

	/// <inheritdoc />
	public Task<RenamePasskeyResult> RenamePasskeyAsync(
		RenamePasskeyRequest request, CancellationToken cancellationToken = default)
	{
		EnsureActorAndIdentity(request, request?.ActorUserId, request?.IdentityUserId);
		ArgumentException.ThrowIfNullOrEmpty(request!.CredentialId);
		EnsureNameAcceptable(request.NewName);

		return JobTrackOperation.TraceAsync(
			"credentials.rename-passkey",
			new CommandContext {
				Actor = request.ActorUserId,
				CorrelationId = request.CorrelationId,
			},
			null,
			() => port.RenamePasskeyAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<RemovePasskeyResult> RemovePasskeyAsync(
		RemovePasskeyRequest request, CancellationToken cancellationToken = default)
	{
		EnsureActorAndIdentity(request, request?.ActorUserId, request?.IdentityUserId);
		ArgumentException.ThrowIfNullOrEmpty(request!.CredentialId);

		return JobTrackOperation.TraceAsync(
			"credentials.remove-passkey",
			new CommandContext {
				Actor = request.ActorUserId,
				CorrelationId = request.CorrelationId,
			},
			null,
			() => port.RemovePasskeyAsync(request, cancellationToken));
	}

	private static void EnsureCredentialMaterialAcceptable(VerifiedPasskey credential)
	{
		var isAcceptable = credential.CredentialId.Length is > 0 and <= PasskeyPolicy.MaximumCredentialIdByteLength
						   && credential.PublicKey.Length is > 0 and <= PasskeyPolicy.MaximumPublicKeyByteLength
						   && credential.SignCount is >= 0 and <= uint.MaxValue
						   && (credential.Transports is null || credential.Transports.Length <= PasskeyPolicy.MaximumTransportsLength)
						   && credential.AttestationObject.Length is > 0 and <= PasskeyPolicy.MaximumAttestationObjectByteLength
						   && credential.ClientDataJson.Length is > 0 and <= PasskeyPolicy.MaximumClientDataJsonByteLength;
		if (!isAcceptable) {
			throw new InvariantViolationException(
				ConstraintIds.PasskeyCredentialMaterialPolicy, "The verified passkey credential material is outside the supported bounds.");
		}
	}

	private static void EnsureNameAcceptable(string name)
	{
		if (!PasskeyPolicy.IsNameAcceptable(name)) {
			throw new InvariantViolationException(ConstraintIds.PasskeyNamePolicy,
				$"A passkey name must be {PasskeyPolicy.MinimumNameLength}–{PasskeyPolicy.MaximumNameLength} characters.");
		}
	}

	private static void EnsureActorAndIdentity(object? request, AppUserId? actorUserId, long? identityUserId)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(identityUserId!.Value);
		if (actorUserId!.Value.IsUnspecified) {
			throw new ArgumentException("Actor user id must be specified.", nameof(request));
		}
	}
}
