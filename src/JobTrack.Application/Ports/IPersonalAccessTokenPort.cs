namespace JobTrack.Application.Ports;

using Abstractions;

/// <summary>
///     The persistence-owned port backing <see cref="ITokenCommands" /> (ADR 0029). Each method is one
///     atomic transaction: the implementation reloads the actor's current roles and applies
///     <see cref="Domain.Authorization.PersonalAccessTokenAccessPolicy" /> itself before writing — the
///     same mutation-safety shape as <see cref="IEmployeeCommandPort" />.
/// </summary>
public interface IPersonalAccessTokenPort
{
	/// <inheritdoc cref="ITokenCommands.IssueAsync" />
	Task<IssuePersonalAccessTokenPersistenceResult> IssueAsync(
		IssuePersonalAccessTokenPersistenceRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="ITokenCommands.ListAsync" />
	Task<EquatableArray<PersonalAccessTokenSummaryResult>> ListAsync(
		ListPersonalAccessTokensRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="ITokenCommands.RevokeAsync" />
	Task RevokeAsync(RevokePersonalAccessTokenRequest request, CancellationToken cancellationToken = default);

	/// <inheritdoc cref="ITokenCommands.RevokeAllAsync" />
	Task RevokeAllAsync(RevokeAllPersonalAccessTokensRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Looks up <paramref name="tokenHash" />, validates it is neither revoked, expired, nor owned by
	///     a disabled/locked account, and updates its last-used instant on success. Returns
	///     <see langword="null" /> for every invalid reason rather than throwing (<see cref="ITokenCommands.TryAuthenticateAsync" />).
	/// </summary>
	Task<AuthenticatedPersonalAccessTokenResult?> TryAuthenticateAsync(
		string tokenHash, CancellationToken cancellationToken = default);
}
