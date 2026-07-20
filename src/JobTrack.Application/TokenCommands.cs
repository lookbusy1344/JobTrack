namespace JobTrack.Application;

using Abstractions;
using NodaTime;
using Ports;

/// <summary>
///     Implements personal access token commands (ADR 0029) by delegating each atomic operation to
///     <see cref="IPersonalAccessTokenPort" />, which owns authorization and the transaction. The
///     plaintext secret is generated and hashed here, mirroring how <see cref="EmployeeCommands" />
///     hashes passwords before the port ever sees them.
/// </summary>
public sealed class TokenCommands : ITokenCommands
{
	private readonly IClock _clock;
	private readonly IPersonalAccessTokenPort _port;

	/// <summary>Creates a <see cref="TokenCommands" /> over the given port.</summary>
	public TokenCommands(IPersonalAccessTokenPort port, IClock clock)
	{
		ArgumentNullException.ThrowIfNull(port);
		ArgumentNullException.ThrowIfNull(clock);

		_port = port;
		_clock = clock;
	}

	/// <inheritdoc />
	public Task<IssuedPersonalAccessTokenResult> IssueAsync(
		IssuePersonalAccessTokenRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"tokens.issue", request.Context, JobTrackOperation.WithUserId(request.TargetUserId),
			async () => {
				var now = _clock.GetCurrentInstant();
				var expiresAt = request.Lifetime is Duration lifetime
					? now + lifetime
					: request.ExpiresAt ?? throw new ArgumentException("Token expiry or lifetime is required.", nameof(request));
				var (plaintextToken, tokenHash) = PersonalAccessTokenSecretGenerator.Generate();
				var persisted = await _port.IssueAsync(
					new() {
						Context = request.Context,
						TargetUserId = request.TargetUserId,
						Label = request.Label,
						TokenHash = tokenHash,
						CreatedAt = now,
						ExpiresAt = expiresAt,
					},
					cancellationToken).ConfigureAwait(false);

				return new IssuedPersonalAccessTokenResult {
					Id = persisted.Id,
					Token = plaintextToken,
					Label = persisted.Label,
					CreatedAt = persisted.CreatedAt,
					ExpiresAt = persisted.ExpiresAt,
				};
			});
	}

	/// <inheritdoc />
	public Task<EquatableArray<PersonalAccessTokenSummaryResult>> ListAsync(
		ListPersonalAccessTokensRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"tokens.list", request.Context, JobTrackOperation.WithUserId(request.TargetUserId),
			() => _port.ListAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task RevokeAsync(RevokePersonalAccessTokenRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"tokens.revoke", request.Context, JobTrackOperation.WithUserId(request.TargetUserId),
			() => _port.RevokeAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task RevokeAllAsync(RevokeAllPersonalAccessTokensRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return JobTrackOperation.TraceAsync(
			"tokens.revoke-all", request.Context, JobTrackOperation.WithUserId(request.TargetUserId),
			() => _port.RevokeAllAsync(request, cancellationToken));
	}

	/// <inheritdoc />
	public Task<AuthenticatedPersonalAccessTokenResult?> TryAuthenticateAsync(
		TryAuthenticatePersonalAccessTokenRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		return _port.TryAuthenticateAsync(PersonalAccessTokenSecretGenerator.Hash(request.Token), cancellationToken);
	}
}
