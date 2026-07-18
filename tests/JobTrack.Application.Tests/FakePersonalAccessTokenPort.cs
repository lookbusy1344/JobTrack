namespace JobTrack.Application.Tests;

using Abstractions;
using Domain.Authorization;
using NodaTime;
using Ports;

/// <summary>
///     An in-memory fake of <see cref="IPersonalAccessTokenPort" /> for application-slice tests (ADR
///     0029). Simulates the authorization guard and expiry validation a real persistence
///     implementation must enforce inside its own transaction.
/// </summary>
internal sealed class FakePersonalAccessTokenPort : IPersonalAccessTokenPort
{
	private readonly Dictionary<AppUserId, bool> _enabled = [];

	private readonly Dictionary<AppUserId, List<EmployeeRole>> _roles = [];
	private readonly List<Token> _tokens = [];
	private long _nextTokenId = 1;

	public Task<IssuePersonalAccessTokenPersistenceResult> IssueAsync(
		IssuePersonalAccessTokenPersistenceRequest request, CancellationToken cancellationToken = default)
	{
		if (!PersonalAccessTokenAccessPolicy.CanIssue(request.Context.Actor, request.TargetUserId)) {
			throw new AuthorizationDeniedException($"Actor {request.Context.Actor} may not issue a token for {request.TargetUserId}.");
		}

		PersonalAccessTokenPolicy.EnsureValidExpiry(request.CreatedAt, request.ExpiresAt);

		var token = new Token {
			Id = new(_nextTokenId++),
			AppUserId = request.TargetUserId,
			TokenHash = request.TokenHash,
			Label = request.Label,
			CreatedAt = request.CreatedAt,
			ExpiresAt = request.ExpiresAt,
		};
		_tokens.Add(token);

		return Task.FromResult(new IssuePersonalAccessTokenPersistenceResult {
			Id = token.Id,
			Label = token.Label,
			CreatedAt = token.CreatedAt,
			ExpiresAt = token.ExpiresAt,
		});
	}

	public Task<EquatableArray<PersonalAccessTokenSummaryResult>> ListAsync(
		ListPersonalAccessTokensRequest request, CancellationToken cancellationToken = default)
	{
		AuthorizeOrThrow(request.Context.Actor, request.TargetUserId);

		return Task.FromResult(EquatableArray.CopyOf(
			_tokens.Where(t => t.AppUserId == request.TargetUserId)
				.OrderByDescending(t => t.CreatedAt)
				.Select(t => new PersonalAccessTokenSummaryResult {
					Id = t.Id,
					Label = t.Label,
					CreatedAt = t.CreatedAt,
					ExpiresAt = t.ExpiresAt,
					RevokedAt = t.RevokedAt,
					LastUsedAt = null,
				})));
	}

	public Task RevokeAsync(RevokePersonalAccessTokenRequest request, CancellationToken cancellationToken = default)
	{
		AuthorizeOrThrow(request.Context.Actor, request.TargetUserId);

		var token = _tokens.FirstOrDefault(t => t.Id == request.TokenId && t.AppUserId == request.TargetUserId)
					?? throw new EntityNotFoundException($"Token {request.TokenId} does not exist for user {request.TargetUserId}.");

		token.RevokedAt ??= SystemClock.Instance.GetCurrentInstant();

		return Task.CompletedTask;
	}

	public Task RevokeAllAsync(RevokeAllPersonalAccessTokensRequest request, CancellationToken cancellationToken = default)
	{
		AuthorizeOrThrow(request.Context.Actor, request.TargetUserId);

		var now = SystemClock.Instance.GetCurrentInstant();
		foreach (var token in _tokens.Where(t => t.AppUserId == request.TargetUserId && t.RevokedAt is null)) {
			token.RevokedAt = now;
		}

		return Task.CompletedTask;
	}

	public Task<AuthenticatedPersonalAccessTokenResult?> TryAuthenticateAsync(
		string tokenHash, CancellationToken cancellationToken = default)
	{
		var now = SystemClock.Instance.GetCurrentInstant();
		var token = _tokens.FirstOrDefault(t => t.TokenHash == tokenHash);
		if (token is null || token.RevokedAt is not null || token.ExpiresAt <= now
			|| !_enabled.GetValueOrDefault(token.AppUserId, true)) {
			return Task.FromResult<AuthenticatedPersonalAccessTokenResult?>(null);
		}

		return Task.FromResult<AuthenticatedPersonalAccessTokenResult?>(
			new() { UserId = token.AppUserId, TokenId = token.Id });
	}

	public void SeedRoles(AppUserId userId, params EmployeeRole[] roles)
	{
		_roles[userId] = [.. roles];
		_enabled.TryAdd(userId, true);
	}

	public void SetEnabled(AppUserId userId, bool enabled) => _enabled[userId] = enabled;

	private void AuthorizeOrThrow(AppUserId actorId, AppUserId targetUserId)
	{
		var roles = _roles.TryGetValue(actorId, out var actorRoles) ? actorRoles : [];

		if (!PersonalAccessTokenAccessPolicy.CanManage(actorId, targetUserId, roles)) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not manage tokens for {targetUserId}.");
		}
	}

	private sealed class Token
	{
		public required PersonalAccessTokenId Id { get; init; }

		public required AppUserId AppUserId { get; init; }

		public required string TokenHash { get; init; }

		public required string Label { get; init; }

		public required Instant CreatedAt { get; init; }

		public required Instant ExpiresAt { get; init; }

		public Instant? RevokedAt { get; set; }
	}
}
