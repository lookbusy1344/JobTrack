namespace JobTrack.Application;

using Abstractions;

/// <summary>
///     Personal access token (PAT) lifecycle commands for the external HTTP API's non-browser client
///     authentication (ADR 0029, ADR 0030): issuing, listing, and revoking bearer credentials that
///     authenticate strictly as their issuing user.
/// </summary>
public interface ITokenCommands
{
	/// <summary>Issues a new token. Always self-service — an actor may only issue a token for themselves.</summary>
	/// <exception cref="AuthorizationDeniedException"><see cref="IssuePersonalAccessTokenRequest.TargetUserId" /> is not the actor.</exception>
	/// <exception cref="InvariantViolationException">
	///     <see cref="IssuePersonalAccessTokenRequest.ExpiresAt" /> is not in the future, or exceeds
	///     <see cref="Domain.Authorization.PersonalAccessTokenPolicy.MaxLifetime" /> (ADR 0029).
	/// </exception>
	Task<IssuedPersonalAccessTokenResult> IssueAsync(
		IssuePersonalAccessTokenRequest request, CancellationToken cancellationToken = default);

	/// <summary>Lists a user's tokens (never including the hash or plaintext).</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor is neither <see cref="ListPersonalAccessTokensRequest.TargetUserId" /> nor holds
	///     <see cref="EmployeeRole.Administrator" />.
	/// </exception>
	Task<EquatableArray<PersonalAccessTokenSummaryResult>> ListAsync(
		ListPersonalAccessTokensRequest request, CancellationToken cancellationToken = default);

	/// <summary>Revokes one token. Idempotent: revoking an already-revoked token is a no-op.</summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor is neither <see cref="RevokePersonalAccessTokenRequest.TargetUserId" /> nor holds
	///     <see cref="EmployeeRole.Administrator" />.
	/// </exception>
	/// <exception cref="EntityNotFoundException">The token does not exist for that owner.</exception>
	Task RevokeAsync(RevokePersonalAccessTokenRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Revokes every live token for a user in one call — used at every security-sensitive account
	///     transition that already revokes web sessions (ADR 0029), not only from a user's own
	///     token-management action.
	/// </summary>
	/// <exception cref="AuthorizationDeniedException">
	///     The actor is neither <see cref="RevokeAllPersonalAccessTokensRequest.TargetUserId" /> nor holds
	///     <see cref="EmployeeRole.Administrator" />.
	/// </exception>
	Task RevokeAllAsync(RevokeAllPersonalAccessTokensRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	///     Validates a presented bearer token and resolves the user it authenticates as, updating its
	///     last-used instant on success. Returns <see langword="null" /> — rather than throwing — for
	///     every invalid-token reason (unknown, revoked, expired, or the owning account disabled): a
	///     presented token failing to authenticate is an ordinary, expected outcome for this call, not
	///     an exceptional one (the <c>Try*</c> naming per CLAUDE.md's expected-absence convention).
	/// </summary>
	Task<AuthenticatedPersonalAccessTokenResult?> TryAuthenticateAsync(
		TryAuthenticatePersonalAccessTokenRequest request, CancellationToken cancellationToken = default);
}
