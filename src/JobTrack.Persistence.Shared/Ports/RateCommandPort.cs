namespace JobTrack.Persistence.Shared.Ports;

using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using Domain.Authorization;
using Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

/// <summary>
///     The provider-neutral body of <see cref="IRateCommandPort" /> (impl plan §7.3 slice 9: add user
///     rates and node overrides). One context/connection/transaction per call over
///     <see cref="IProviderWriteOperations" />, reloading the actor's current roles and applying
///     <see cref="RateAccessPolicy" /> itself before writing. Same-user (and same-node-and-user)
///     overlap is enforced purely by schema version 0011's own database constraints -- PostgreSQL's
///     GiST exclusion constraints, SQLite's equivalent immediate triggers -- so a conflict is caught
///     by translating whatever driver exception
///     <see cref="IProviderWriteOperations.ClassifyWriteConflict" /> identifies, not by taking a lock:
///     rate data is not one of ADR 0012's lock domains.
/// </summary>
internal sealed class RateCommandPort(IProviderWriteOperations provider, IClock clock) : IRateCommandPort
{
	/// <inheritdoc />
	public async Task<UserCostRateResult> AddUserCostRateAsync(
		AddUserCostRateRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await provider.CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await provider.BeginWriteTransactionAsync(context, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		await EnsureEmployeeExistsAsync(context, request.UserId, cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, now, cancellationToken).ConfigureAwait(false);

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
		catch (Exception ex) when (provider.ClassifyWriteConflict(ex) is WriteConflictKind.RangeOverlap or WriteConflictKind.UniquenessViolation) {
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
		await using var context = await provider.CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await provider.BeginWriteTransactionAsync(context, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		await EnsureEmployeeExistsAsync(context, request.UserId, cancellationToken).ConfigureAwait(false);
		await EnsureNodeExistsAsync(context, request.Override.NodeId, cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, now, cancellationToken).ConfigureAwait(false);

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
		catch (Exception ex) when (provider.ClassifyWriteConflict(ex) is WriteConflictKind.RangeOverlap or WriteConflictKind.UniquenessViolation) {
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
		await using var context = await provider.CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await provider.BeginWriteTransactionAsync(context, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		var entity = await LoadTrackedUserCostRateAsync(context, request.RateId, cancellationToken).ConfigureAwait(false);
		EnsureUserMatchesOrThrow(entity.UserId, request.UserId, request.RateId.Value);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, now, cancellationToken).ConfigureAwait(false);
		CheckVersionOrThrow(entity.RowVersion, request.Version);

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
		catch (Exception ex) when (provider.ClassifyWriteConflict(ex) is WriteConflictKind.RangeOverlap or WriteConflictKind.UniquenessViolation) {
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
		await using var context = await provider.CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await provider.BeginWriteTransactionAsync(context, cancellationToken).ConfigureAwait(false);

		var now = clock.GetCurrentInstant();
		var entity = await LoadTrackedNodeRateOverrideAsync(context, request.OverrideId, cancellationToken).ConfigureAwait(false);
		EnsureUserMatchesOrThrow(entity.UserId, request.UserId, request.OverrideId.Value);
		await EnsureNodeExistsAsync(context, request.Override.NodeId, cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, now, cancellationToken).ConfigureAwait(false);
		CheckVersionOrThrow(entity.RowVersion, request.Version);

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
		catch (Exception ex) when (provider.ClassifyWriteConflict(ex) is WriteConflictKind.RangeOverlap or WriteConflictKind.UniquenessViolation) {
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

	private static async Task EnsureEmployeeExistsAsync(
		DbContext context, AppUserId userId, CancellationToken cancellationToken)
	{
		if (!await context.Set<AppUserEntity>().AsNoTracking()
						  .AnyAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false)) {
			throw new EntityNotFoundException($"Employee {userId} does not exist.");
		}
	}

	private static async Task EnsureNodeExistsAsync(
		DbContext context, JobNodeId nodeId, CancellationToken cancellationToken)
	{
		if (!await context.Set<JobNodeEntity>().AsNoTracking()
						  .AnyAsync(n => n.Id == nodeId, cancellationToken).ConfigureAwait(false)) {
			throw new EntityNotFoundException($"Job node {nodeId} does not exist.");
		}
	}

	private static async Task AuthorizeOrThrowAsync(
		DbContext context, AppUserId actorId, Instant now, CancellationToken cancellationToken)
	{
		var actorRoles = await ActorAccountState.LoadRolesAsync(context, actorId, now, cancellationToken).ConfigureAwait(false);

		if (!RateAccessPolicy.CanManage(actorRoles)) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not manage rate data.");
		}
	}

	private static async Task<UserCostRateEntity> LoadTrackedUserCostRateAsync(
		DbContext context, UserCostRateId rateId, CancellationToken cancellationToken) =>
		await context.Set<UserCostRateEntity>().FirstOrDefaultAsync(r => r.Id == rateId, cancellationToken).ConfigureAwait(false)
		?? throw new EntityNotFoundException($"User cost rate {rateId} does not exist.");

	private static async Task<NodeRateOverrideEntity> LoadTrackedNodeRateOverrideAsync(
		DbContext context, NodeRateOverrideId overrideId, CancellationToken cancellationToken) =>
		await context.Set<NodeRateOverrideEntity>().FirstOrDefaultAsync(o => o.Id == overrideId, cancellationToken).ConfigureAwait(false)
		?? throw new EntityNotFoundException($"Node rate override {overrideId} does not exist.");

	/// <summary>
	///     A nested route's parent identifier must actually match the row's owner, or the mismatch is
	///     treated identically to a nonexistent row (matching <c>WorkSessionCommandPort</c>'s
	///     <c>EnsureLeafMatchesOrThrow</c>) -- checked before authorization, alongside the existence check
	///     the load helpers already perform.
	/// </summary>
	private static void EnsureUserMatchesOrThrow(AppUserId actualUserId, AppUserId? expectedUserId, long rowId)
	{
		if (expectedUserId is AppUserId userId && actualUserId != userId) {
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
