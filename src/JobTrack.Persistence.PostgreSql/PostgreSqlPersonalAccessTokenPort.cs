namespace JobTrack.Persistence.PostgreSql;

using Abstractions;
using Application;
using Application.Ports;
using Domain.Authorization;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Shared;
using Shared.Entities;

/// <summary>
///     PostgreSQL implementation of <see cref="IPersonalAccessTokenPort" /> (ADR 0029). One
///     <see cref="PostgreSqlJobTrackDbContext" />/connection/transaction per call, reloading the
///     actor's current roles and applying <see cref="PersonalAccessTokenAccessPolicy" /> itself before
///     writing, matching the same mutation-safety shape as <see cref="Shared.Ports.EmployeeCommandPort" />.
/// </summary>
internal sealed class PostgreSqlPersonalAccessTokenPort : IPersonalAccessTokenPort
{
	private readonly NpgsqlDataSource authenticationDataSource;
	private readonly MicrosecondTruncatingClock clock;
	private readonly NpgsqlDataSource managementDataSource;

	public PostgreSqlPersonalAccessTokenPort(NpgsqlDataSource dataSource, IClock clock)
		: this(dataSource, dataSource, clock) { }

	/// <summary>Creates the port over the given pooled <see cref="NpgsqlDataSource" />.</summary>
	public PostgreSqlPersonalAccessTokenPort(
		NpgsqlDataSource managementDataSource,
		NpgsqlDataSource authenticationDataSource,
		IClock clock)
	{
		this.managementDataSource = managementDataSource;
		this.authenticationDataSource = authenticationDataSource;
		this.clock = new MicrosecondTruncatingClock(clock);
	}

	/// <inheritdoc />
	public async Task<IssuePersonalAccessTokenPersistenceResult> IssueAsync(
		IssuePersonalAccessTokenPersistenceRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateManagementContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await AuthorizeIssueOrThrowAsync(context, request.Context.Actor, request.TargetUserId, request.CreatedAt, cancellationToken)
			.ConfigureAwait(false);

		PersonalAccessTokenPolicy.EnsureValidExpiry(request.CreatedAt, request.ExpiresAt);

		var tokenId = await PostgreSqlPersonalAccessTokenFunctions.IssueAsync(
																	  context, request.TargetUserId, request.TokenHash, request.Label, request.CreatedAt, request.ExpiresAt, cancellationToken)
																  .ConfigureAwait(false);

		AuditEventWriter.Add(
			context, request.Context.Actor, request.CreatedAt, "issue-personal-access-token", "personal_access_token",
			tokenId.Value, request.Context.CorrelationId, null, null,
			new Dictionary<string, string?> {
				["label"] = request.Label,
			});

		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		return new() {
			Id = tokenId,
			Label = request.Label,
			CreatedAt = request.CreatedAt,
			ExpiresAt = request.ExpiresAt,
		};
	}

	/// <inheritdoc />
	public async Task<EquatableArray<PersonalAccessTokenSummaryResult>> ListAsync(
		ListPersonalAccessTokensRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateManagementContext();
		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.TargetUserId, clock.GetCurrentInstant(), cancellationToken)
			.ConfigureAwait(false);

		var tokens = await PostgreSqlPersonalAccessTokenFunctions.ListAsync(context, request.TargetUserId, cancellationToken)
																 .ConfigureAwait(false);

		return [
			.. tokens.Select(t => new PersonalAccessTokenSummaryResult {
				Id = new(t.Id),
				Label = t.Label,
				CreatedAt = t.CreatedAt,
				ExpiresAt = t.ExpiresAt,
				RevokedAt = t.RevokedAt,
				LastUsedAt = t.LastUsedAt,
			}),
		];
	}

	/// <inheritdoc />
	public async Task RevokeAsync(RevokePersonalAccessTokenRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateManagementContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.TargetUserId, now, cancellationToken).ConfigureAwait(false);

		var result = await PostgreSqlPersonalAccessTokenFunctions.RevokeAsync(
			context, request.TokenId, request.TargetUserId, now, cancellationToken).ConfigureAwait(false);
		if (!result.Found) {
			throw new EntityNotFoundException($"Token {request.TokenId} does not exist for user {request.TargetUserId}.");
		}

		if (result.NewlyRevoked) {
			AuditEventWriter.Add(
				context, request.Context.Actor, now, "revoke-personal-access-token", "personal_access_token",
				request.TokenId.Value, request.Context.CorrelationId, null, null, null);

			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task RevokeAllAsync(RevokeAllPersonalAccessTokensRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateManagementContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.TargetUserId, now, cancellationToken).ConfigureAwait(false);

		var revoked = await PostgreSqlPersonalAccessTokenFunctions.RevokeAllForUserAsync(context, request.TargetUserId, now, cancellationToken)
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
	/// <remarks>
	///     <c>pat_try_authenticate</c> touches <c>last_used_at</c> as soon as the token itself is
	///     unrevoked and unexpired, before this method's separate owner-enabled/lockout check --
	///     unchanged from the pre-§2.6 behaviour in spirit (the token row and the owner row were
	///     always two separate reads), but a token whose owner is currently disabled/locked out now
	///     still records the attempt as a "use" even though authentication still fails overall.
	/// </remarks>
	public async Task<AuthenticatedPersonalAccessTokenResult?> TryAuthenticateAsync(
		string tokenHash, CancellationToken cancellationToken = default)
	{
		await using var context = CreateAuthenticationContext();

		var now = clock.GetCurrentInstant();
		var token = await PostgreSqlPersonalAccessTokenFunctions.TryAuthenticateAsync(context, tokenHash, now, cancellationToken)
																.ConfigureAwait(false);
		if (token is null) {
			return null;
		}

		return new() {
			UserId = new(token.AppUserId),
			TokenId = new(token.Id),
		};
	}

	private PostgreSqlJobTrackDbContext CreateManagementContext() => CreateContext(managementDataSource);

	private PostgreSqlJobTrackDbContext CreateAuthenticationContext() => CreateContext(authenticationDataSource);

	private static PostgreSqlJobTrackDbContext CreateContext(NpgsqlDataSource dataSource)
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
					  .UseNpgsql(dataSource, o => o.UseNodaTime())
					  .Options;

		return new(options);
	}

	private static async Task AuthorizeOrThrowAsync(
		PostgreSqlJobTrackDbContext context, AppUserId actorId, AppUserId targetUserId, Instant now, CancellationToken cancellationToken)
	{
		var actorIdentityUser = await LoadActingIdentityUserAsync(context, actorId, now, cancellationToken).ConfigureAwait(false);

		var actorRoles = await context.Set<IdentityUserRoleEntity>().AsNoTracking()
									  .Where(ur => ur.IdentityUserId == actorIdentityUser.Id)
									  .Select(ur => (EmployeeRole)ur.IdentityRoleId)
									  .ToArrayAsync(cancellationToken).ConfigureAwait(false);

		if (!PersonalAccessTokenAccessPolicy.CanManage(actorId, targetUserId, actorRoles)) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not manage tokens for {targetUserId}.");
		}
	}

	private static async Task AuthorizeIssueOrThrowAsync(
		PostgreSqlJobTrackDbContext context, AppUserId actorId, AppUserId targetUserId, Instant now, CancellationToken cancellationToken)
	{
		_ = await LoadActingIdentityUserAsync(context, actorId, now, cancellationToken).ConfigureAwait(false);

		if (!PersonalAccessTokenAccessPolicy.CanIssue(actorId, targetUserId)) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not issue a token for {targetUserId}.");
		}
	}

	private static async Task<PatActorAccountState> LoadActingIdentityUserAsync(
		PostgreSqlJobTrackDbContext context, AppUserId actorId, Instant now, CancellationToken cancellationToken)
	{
		var actorIdentityUser = await context.Set<IdentityUserEntity>().AsNoTracking()
											 .Where(iu => iu.AppUserId == actorId)
											 .Select(iu => new PatActorAccountState(iu.Id, iu.IsEnabled, iu.LockoutEnabled, iu.LockoutEnd))
											 .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
								?? throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		ActorAccountState.EnsureMayAct(
			actorIdentityUser.IsEnabled, actorIdentityUser.LockoutEnabled, actorIdentityUser.LockoutEnd, actorId, now);

		return actorIdentityUser;
	}
}

internal sealed record PatActorAccountState(long Id, bool IsEnabled, bool LockoutEnabled, Instant? LockoutEnd);
