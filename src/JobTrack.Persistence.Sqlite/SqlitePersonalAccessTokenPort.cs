namespace JobTrack.Persistence.Sqlite;

using System.Data;
using Abstractions;
using Application;
using Application.Ports;
using Domain.Authorization;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared;
using Shared.Entities;

/// <summary>
///     SQLite implementation of <see cref="IPersonalAccessTokenPort" /> (ADR 0029). One
///     <see cref="SqliteJobTrackDbContext" />/connection/transaction per call; <see cref="IsolationLevel.Serializable" />
///     starts a <c>BEGIN IMMEDIATE</c> transaction matching <see cref="SqliteEmployeeCommandPort" />'s
///     established technique.
/// </summary>
internal sealed class SqlitePersonalAccessTokenPort : IPersonalAccessTokenPort
{
	private readonly string connectionString;

	/// <summary>Creates the port over the given SQLite connection string.</summary>
	public SqlitePersonalAccessTokenPort(string connectionString) => this.connectionString = connectionString;

	/// <inheritdoc />
	public async Task<IssuePersonalAccessTokenPersistenceResult> IssueAsync(
		IssuePersonalAccessTokenPersistenceRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		await AuthorizeIssueOrThrowAsync(context, request.Context.Actor, request.TargetUserId, cancellationToken).ConfigureAwait(false);

		PersonalAccessTokenPolicy.EnsureValidExpiry(request.CreatedAt, request.ExpiresAt);

		var token = new PersonalAccessTokenEntity {
			Id = default,
			AppUserId = request.TargetUserId,
			TokenHash = request.TokenHash,
			Label = request.Label,
			CreatedAt = request.CreatedAt,
			ExpiresAt = request.ExpiresAt,
		};
		_ = context.Add(token);
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		AuditEventWriter.Add(
			context, request.Context.Actor, request.CreatedAt, "issue-personal-access-token", "personal_access_token",
			token.Id.Value, request.Context.CorrelationId, null, null,
			new Dictionary<string, string?> { ["label"] = request.Label });

		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		return new() {
			Id = token.Id,
			Label = token.Label,
			CreatedAt = token.CreatedAt,
			ExpiresAt = token.ExpiresAt,
		};
	}

	/// <inheritdoc />
	public async Task<EquatableArray<PersonalAccessTokenSummaryResult>> ListAsync(
		ListPersonalAccessTokensRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.TargetUserId, cancellationToken).ConfigureAwait(false);

		var tokens = await context.Set<PersonalAccessTokenEntity>().AsNoTracking()
			.Where(t => t.AppUserId == request.TargetUserId)
			.OrderByDescending(t => t.CreatedAt)
			.Select(t => new PersonalAccessTokenSummaryResult {
				Id = t.Id,
				Label = t.Label,
				CreatedAt = t.CreatedAt,
				ExpiresAt = t.ExpiresAt,
				RevokedAt = t.RevokedAt,
				LastUsedAt = t.LastUsedAt,
			})
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);

		return [.. tokens];
	}

	/// <inheritdoc />
	public async Task RevokeAsync(RevokePersonalAccessTokenRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.TargetUserId, cancellationToken).ConfigureAwait(false);

		var token = await context.Set<PersonalAccessTokenEntity>()
						.FirstOrDefaultAsync(
							t => t.Id == request.TokenId && t.AppUserId == request.TargetUserId, cancellationToken)
						.ConfigureAwait(false)
					?? throw new EntityNotFoundException($"Token {request.TokenId} does not exist for user {request.TargetUserId}.");

		if (token.RevokedAt is null) {
			var now = SystemClock.Instance.GetCurrentInstant();
			token.RevokedAt = now;

			AuditEventWriter.Add(
				context, request.Context.Actor, now, "revoke-personal-access-token", "personal_access_token",
				token.Id.Value, request.Context.CorrelationId, null, null, null);

			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task RevokeAllAsync(RevokeAllPersonalAccessTokensRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.TargetUserId, cancellationToken).ConfigureAwait(false);

		var now = SystemClock.Instance.GetCurrentInstant();
		var revoked = await PersonalAccessTokenRevocation.RevokeAllForUserAsync(context, request.TargetUserId, now, cancellationToken)
			.ConfigureAwait(false);

		if (revoked > 0) {
			AuditEventWriter.Add(
				context, request.Context.Actor, now, "revoke-all-personal-access-tokens", "app_user",
				request.TargetUserId.Value, request.Context.CorrelationId, null, null, null);
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task<AuthenticatedPersonalAccessTokenResult?> TryAuthenticateAsync(
		string tokenHash, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);

		var now = SystemClock.Instance.GetCurrentInstant();
		var token = await context.Set<PersonalAccessTokenEntity>()
			.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken).ConfigureAwait(false);
		if (token is null || token.RevokedAt is not null || token.ExpiresAt <= now) {
			return null;
		}

		var owner = await context.Set<IdentityUserEntity>().AsNoTracking()
			.FirstOrDefaultAsync(iu => iu.AppUserId == token.AppUserId, cancellationToken).ConfigureAwait(false);
		if (owner is null || !owner.IsEnabled || (owner.LockoutEnabled && owner.LockoutEnd is Instant lockoutEnd && lockoutEnd > now)) {
			return null;
		}

		token.LastUsedAt = now;
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		return new() { UserId = token.AppUserId, TokenId = token.Id };
	}

	private Task<SqliteJobTrackDbContext> CreateOpenContextAsync(CancellationToken cancellationToken) =>
		SqliteDbContextFactory.CreateOpenContextAsync(connectionString, cancellationToken);

	private static async Task AuthorizeOrThrowAsync(
		SqliteJobTrackDbContext context, AppUserId actorId, AppUserId targetUserId, CancellationToken cancellationToken)
	{
		var actorIdentityUser = await LoadActingIdentityUserAsync(context, actorId, cancellationToken).ConfigureAwait(false);

		var actorRoles = await context.Set<IdentityUserRoleEntity>().AsNoTracking()
			.Where(ur => ur.IdentityUserId == actorIdentityUser.Id)
			.Select(ur => (EmployeeRole)ur.IdentityRoleId)
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);

		if (!PersonalAccessTokenAccessPolicy.CanManage(actorId, targetUserId, actorRoles)) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not manage tokens for {targetUserId}.");
		}
	}

	private static async Task AuthorizeIssueOrThrowAsync(
		SqliteJobTrackDbContext context, AppUserId actorId, AppUserId targetUserId, CancellationToken cancellationToken)
	{
		_ = await LoadActingIdentityUserAsync(context, actorId, cancellationToken).ConfigureAwait(false);

		if (!PersonalAccessTokenAccessPolicy.CanIssue(actorId, targetUserId)) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not issue a token for {targetUserId}.");
		}
	}

	private static async Task<IdentityUserEntity> LoadActingIdentityUserAsync(
		SqliteJobTrackDbContext context, AppUserId actorId, CancellationToken cancellationToken)
	{
		var actorIdentityUser = await context.Set<IdentityUserEntity>().AsNoTracking()
									.FirstOrDefaultAsync(iu => iu.AppUserId == actorId, cancellationToken).ConfigureAwait(false)
								?? throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		ActorAccountState.EnsureMayAct(actorIdentityUser, actorId, SystemClock.Instance.GetCurrentInstant());

		return actorIdentityUser;
	}
}
