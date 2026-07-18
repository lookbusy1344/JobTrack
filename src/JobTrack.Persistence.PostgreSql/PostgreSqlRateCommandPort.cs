namespace JobTrack.Persistence.PostgreSql;

using System.Globalization;
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
///     PostgreSQL implementation of <see cref="IRateCommandPort" /> (impl plan §7.3 slice 9: add user
///     rates and node overrides). One <see cref="PostgreSqlJobTrackDbContext" />/connection/transaction
///     per call, reloading the actor's current roles and applying <see cref="RateAccessPolicy" /> itself
///     before writing. Same-user (and same-node-and-user) overlap is enforced purely by schema version
///     0011's GiST exclusion constraints, so a conflict is caught by translating the resulting
///     <see cref="PostgresException" />, not by taking a lock -- rate data is not one of ADR 0012's lock
///     domains.
/// </summary>
internal sealed class PostgreSqlRateCommandPort : IRateCommandPort
{
	private readonly NpgsqlDataSource dataSource;

	/// <summary>Creates the port over the given pooled <see cref="NpgsqlDataSource" />.</summary>
	public PostgreSqlRateCommandPort(NpgsqlDataSource dataSource) => this.dataSource = dataSource;

	/// <inheritdoc />
	public async Task<UserCostRateResult> AddUserCostRateAsync(
		AddUserCostRateRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await EnsureEmployeeExistsAsync(context, request.UserId, cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, cancellationToken).ConfigureAwait(false);

		var now = SystemClock.Instance.GetCurrentInstant();
		var entity = new UserCostRateEntity {
			Id = default,
			UserId = request.UserId,
			EffectiveStart = request.Rate.EffectiveStart,
			EffectiveEnd = request.Rate.EffectiveEnd,
			Rate = request.Rate.Rate,
			ChangedAt = now,
			RowVersion = 1,
		};
		_ = context.Add(entity);

		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			AuditEventWriter.Add(
				context, request.Context.Actor, now, "add-user-cost-rate", "user_cost_rate", entity.Id.Value, request.Context.CorrelationId,
				null, null,
				new Dictionary<string, string?> {
					["effective_start"] = entity.EffectiveStart.ToString(),
					["effective_end"] = entity.EffectiveEnd?.ToString(),
					["amount_per_hour"] = entity.Rate.AmountPerHour.ToString(CultureInfo.InvariantCulture),
				});
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (FindOverlapException(ex) is not null) {
			throw new InvariantViolationException(
				"user-cost-rate-overlap", "This cost rate's effective range overlaps another for this employee.", ex);
		}

		return new() {
			Id = entity.Id,
			UserId = entity.UserId,
			Rate = request.Rate,
			ChangedAt = entity.ChangedAt,
			Version = entity.RowVersion,
		};
	}

	/// <inheritdoc />
	public async Task<NodeRateOverrideResult> AddNodeRateOverrideAsync(
		AddNodeRateOverrideRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await EnsureEmployeeExistsAsync(context, request.UserId, cancellationToken).ConfigureAwait(false);
		await EnsureNodeExistsAsync(context, request.Override.NodeId, cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, cancellationToken).ConfigureAwait(false);

		var now = SystemClock.Instance.GetCurrentInstant();
		var entity = new NodeRateOverrideEntity {
			Id = default,
			NodeId = request.Override.NodeId,
			UserId = request.UserId,
			EffectiveStart = request.Override.EffectiveStart,
			EffectiveEnd = request.Override.EffectiveEnd,
			Rate = request.Override.Rate,
			ChangedAt = now,
			RowVersion = 1,
		};
		_ = context.Add(entity);

		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			AuditEventWriter.Add(
				context, request.Context.Actor, now, "add-node-rate-override", "node_rate_override", entity.Id.Value,
				request.Context.CorrelationId, null, null,
				new Dictionary<string, string?> {
					["node_id"] = entity.NodeId.Value.ToString(CultureInfo.InvariantCulture),
					["effective_start"] = entity.EffectiveStart.ToString(),
					["effective_end"] = entity.EffectiveEnd?.ToString(),
					["amount_per_hour"] = entity.Rate.AmountPerHour.ToString(CultureInfo.InvariantCulture),
				});
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (FindOverlapException(ex) is not null) {
			throw new InvariantViolationException(
				"node-rate-override-overlap", "This override's effective range overlaps another for this node and employee.", ex);
		}

		return new() {
			Id = entity.Id,
			UserId = entity.UserId,
			Override = request.Override,
			ChangedAt = entity.ChangedAt,
			Version = entity.RowVersion,
		};
	}

	/// <inheritdoc />
	public async Task<UserCostRateResult> CorrectUserCostRateAsync(
		CorrectUserCostRateRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		var entity = await LoadTrackedUserCostRateAsync(context, request.RateId, cancellationToken).ConfigureAwait(false);
		EnsureUserMatchesOrThrow(entity.UserId, request.UserId, request.RateId.Value);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, cancellationToken).ConfigureAwait(false);
		CheckVersionOrThrow(entity.RowVersion, request.Version);

		var now = SystemClock.Instance.GetCurrentInstant();
		var before = new Dictionary<string, string?> {
			["effective_start"] = entity.EffectiveStart.ToString(),
			["effective_end"] = entity.EffectiveEnd?.ToString(),
			["amount_per_hour"] = entity.Rate.AmountPerHour.ToString(CultureInfo.InvariantCulture),
		};

		entity.EffectiveStart = request.Rate.EffectiveStart;
		entity.EffectiveEnd = request.Rate.EffectiveEnd;
		entity.Rate = request.Rate.Rate;
		entity.ChangedAt = now;
		entity.RowVersion += 1;

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "correct-user-cost-rate", "user_cost_rate", entity.Id.Value,
			request.Context.CorrelationId, request.Reason, before,
			new Dictionary<string, string?> {
				["effective_start"] = entity.EffectiveStart.ToString(),
				["effective_end"] = entity.EffectiveEnd?.ToString(),
				["amount_per_hour"] = entity.Rate.AmountPerHour.ToString(CultureInfo.InvariantCulture),
			});

		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException ex) {
			throw new ConcurrencyConflictException(
				$"Expected version {request.Version} for user cost rate {request.RateId} did not match its current version.", ex);
		}
		catch (Exception ex) when (FindOverlapException(ex) is not null) {
			throw new InvariantViolationException(
				"user-cost-rate-overlap", "This cost rate's effective range overlaps another for this employee.", ex);
		}

		return new() {
			Id = entity.Id,
			UserId = entity.UserId,
			Rate = request.Rate,
			ChangedAt = entity.ChangedAt,
			Version = entity.RowVersion,
		};
	}

	/// <inheritdoc />
	public async Task<NodeRateOverrideResult> CorrectNodeRateOverrideAsync(
		CorrectNodeRateOverrideRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		var entity = await LoadTrackedNodeRateOverrideAsync(context, request.OverrideId, cancellationToken).ConfigureAwait(false);
		EnsureUserMatchesOrThrow(entity.UserId, request.UserId, request.OverrideId.Value);
		await EnsureNodeExistsAsync(context, request.Override.NodeId, cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, cancellationToken).ConfigureAwait(false);
		CheckVersionOrThrow(entity.RowVersion, request.Version);

		var now = SystemClock.Instance.GetCurrentInstant();
		var before = new Dictionary<string, string?> {
			["node_id"] = entity.NodeId.Value.ToString(CultureInfo.InvariantCulture),
			["effective_start"] = entity.EffectiveStart.ToString(),
			["effective_end"] = entity.EffectiveEnd?.ToString(),
			["amount_per_hour"] = entity.Rate.AmountPerHour.ToString(CultureInfo.InvariantCulture),
		};

		entity.NodeId = request.Override.NodeId;
		entity.EffectiveStart = request.Override.EffectiveStart;
		entity.EffectiveEnd = request.Override.EffectiveEnd;
		entity.Rate = request.Override.Rate;
		entity.ChangedAt = now;
		entity.RowVersion += 1;

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "correct-node-rate-override", "node_rate_override", entity.Id.Value,
			request.Context.CorrelationId, request.Reason, before,
			new Dictionary<string, string?> {
				["node_id"] = entity.NodeId.Value.ToString(CultureInfo.InvariantCulture),
				["effective_start"] = entity.EffectiveStart.ToString(),
				["effective_end"] = entity.EffectiveEnd?.ToString(),
				["amount_per_hour"] = entity.Rate.AmountPerHour.ToString(CultureInfo.InvariantCulture),
			});

		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException ex) {
			throw new ConcurrencyConflictException(
				$"Expected version {request.Version} for node rate override {request.OverrideId} did not match its current version.", ex);
		}
		catch (Exception ex) when (FindOverlapException(ex) is not null) {
			throw new InvariantViolationException(
				"node-rate-override-overlap", "This override's effective range overlaps another for this node and employee.", ex);
		}

		return new() {
			Id = entity.Id,
			UserId = entity.UserId,
			Override = request.Override,
			ChangedAt = entity.ChangedAt,
			Version = entity.RowVersion,
		};
	}

	/// <summary>
	///     Schema version 0011's user-cost-rate and node-rate-override exclusion constraints have no
	///     partial-unique-index backstop the way schema version 0007's work-session table does, so a
	///     violation surfaces here exactly as PostgreSQL reports it (<c>ExclusionViolation</c>, or
	///     <c>DeadlockDetected</c> under genuine concurrent interleaving); EF's Npgsql execution
	///     strategy still re-wraps either in an outer <see cref="InvalidOperationException" /> even on a
	///     single, non-retried attempt, so this walks the whole <see cref="Exception.InnerException" />
	///     chain rather than checking one level (matching <c>PostgreSqlScheduleCommandPort</c>'s own
	///     helper).
	/// </summary>
	private static PostgresException? FindOverlapException(Exception? ex) =>
		ex switch {
			null => null,
			PostgresException pg when IsOverlapSqlState(pg.SqlState) => pg,
			_ => FindOverlapException(ex.InnerException),
		};

	private static bool IsOverlapSqlState(string? sqlState) =>
		sqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ExclusionViolation or PostgresErrorCodes.DeadlockDetected;

	private PostgreSqlJobTrackDbContext CreateContext()
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, o => o.UseNodaTime())
			.Options;

		return new(options);
	}

	private static async Task EnsureEmployeeExistsAsync(
		PostgreSqlJobTrackDbContext context, AppUserId userId, CancellationToken cancellationToken)
	{
		if (!await context.Set<AppUserEntity>().AsNoTracking()
				.AnyAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false)) {
			throw new EntityNotFoundException($"Employee {userId} does not exist.");
		}
	}

	private static async Task EnsureNodeExistsAsync(
		PostgreSqlJobTrackDbContext context, JobNodeId nodeId, CancellationToken cancellationToken)
	{
		if (!await context.Set<JobNodeEntity>().AsNoTracking()
				.AnyAsync(n => n.Id == nodeId, cancellationToken).ConfigureAwait(false)) {
			throw new EntityNotFoundException($"Job node {nodeId} does not exist.");
		}
	}

	private static async Task AuthorizeOrThrowAsync(
		PostgreSqlJobTrackDbContext context, AppUserId actorId, CancellationToken cancellationToken)
	{
		var actorRoles = await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);

		if (!RateAccessPolicy.CanManage(actorRoles)) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not manage rate data.");
		}
	}

	private static async Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		PostgreSqlJobTrackDbContext context, AppUserId actorId, CancellationToken cancellationToken)
	{
		var actorIdentityUser = await context.Set<IdentityUserEntity>().AsNoTracking()
									.FirstOrDefaultAsync(iu => iu.AppUserId == actorId, cancellationToken).ConfigureAwait(false)
								?? throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		ActorAccountState.EnsureMayAct(actorIdentityUser, actorId, SystemClock.Instance.GetCurrentInstant());

		var roles = await context.Set<IdentityUserRoleEntity>().AsNoTracking()
			.Where(ur => ur.IdentityUserId == actorIdentityUser.Id)
			.Select(ur => (EmployeeRole)ur.IdentityRoleId)
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);

		return [.. roles];
	}

	private static async Task<UserCostRateEntity> LoadTrackedUserCostRateAsync(
		PostgreSqlJobTrackDbContext context, UserCostRateId rateId, CancellationToken cancellationToken) =>
		await context.Set<UserCostRateEntity>().FirstOrDefaultAsync(r => r.Id == rateId, cancellationToken).ConfigureAwait(false)
		?? throw new EntityNotFoundException($"User cost rate {rateId} does not exist.");

	private static async Task<NodeRateOverrideEntity> LoadTrackedNodeRateOverrideAsync(
		PostgreSqlJobTrackDbContext context, NodeRateOverrideId overrideId, CancellationToken cancellationToken) =>
		await context.Set<NodeRateOverrideEntity>().FirstOrDefaultAsync(o => o.Id == overrideId, cancellationToken).ConfigureAwait(false)
		?? throw new EntityNotFoundException($"Node rate override {overrideId} does not exist.");

	/// <summary>
	///     A nested route's parent identifier must actually match the row's owner, or the mismatch is
	///     treated identically to a nonexistent row (matching <c>PostgreSqlWorkSessionCommandPort</c>'s
	///     <c>EnsureLeafMatchesOrThrow</c>) -- checked before authorization, alongside the existence check
	///     the load helpers already perform.
	/// </summary>
	private static void EnsureUserMatchesOrThrow(AppUserId actualUserId, AppUserId? expectedUserId, long rowId)
	{
		if (expectedUserId is { } userId && actualUserId != userId) {
			throw new EntityNotFoundException($"Rate row {rowId} does not belong to employee {userId}.");
		}
	}

	private static void CheckVersionOrThrow(long currentVersion, long expectedVersion)
	{
		if (currentVersion != expectedVersion) {
			throw new ConcurrencyConflictException(
				$"Expected version {expectedVersion} but the current version is {currentVersion}.");
		}
	}
}
