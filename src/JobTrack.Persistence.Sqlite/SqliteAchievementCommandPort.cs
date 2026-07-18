namespace JobTrack.Persistence.Sqlite;

using System.Data;
using Abstractions;
using Application;
using Application.Ports;
using Domain.Authorization;
using Domain.Hierarchy;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared;
using Shared.Entities;

/// <summary>
///     SQLite implementation of <see cref="IAchievementCommandPort" /> (impl plan §7.3 slice 7: change
///     achievement subject to prerequisite gates, ADR 0001). One <see cref="SqliteJobTrackDbContext" />/
///     connection/transaction per call; SQLite has no advisory lock or stored function, so
///     <see cref="IsolationLevel.Serializable" /> starts a <c>BEGIN IMMEDIATE</c> transaction that
///     serializes concurrent writes through SQLite's single-writer model (matches
///     <see cref="SqliteWorkSessionCommandPort" />'s established use of the same technique). The state
///     machine is enforced purely application-side against a single tracked <c>leaf_work</c> row
///     guarded by its own concurrency token, not by a trigger.
/// </summary>
internal sealed class SqliteAchievementCommandPort : IAchievementCommandPort
{
	private readonly string connectionString;

	/// <summary>Creates the port over the given SQLite connection string.</summary>
	public SqliteAchievementCommandPort(string connectionString) => this.connectionString = connectionString;

	/// <inheritdoc />
	public async Task<LeafWorkResult> SetAchievementAsync(SetAchievementRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

		var leafWork = await context.Set<LeafWorkEntity>()
						   .FirstOrDefaultAsync(lw => lw.JobNodeId == request.JobNodeId, cancellationToken).ConfigureAwait(false)
					   ?? throw new EntityNotFoundException($"Job node {request.JobNodeId} has no LeafWork attached.");

		var isReopening = AchievementTransitions.IsReopening(leafWork.Achievement, request.NewAchievement);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.JobNodeId, isReopening, cancellationToken).ConfigureAwait(false);
		CheckVersionOrThrow(leafWork.RowVersion, request.Version);

		if (!AchievementTransitions.IsPermitted(leafWork.Achievement, request.NewAchievement)) {
			throw new InvariantViolationException(
				"achievement-transition-not-permitted",
				$"Cannot transition from {leafWork.Achievement} to {request.NewAchievement}.");
		}

		if (AchievementTransitions.IsCompletedState(request.NewAchievement)
			&& !await LeafReadiness.IsReadyAsync(context, request.JobNodeId, cancellationToken).ConfigureAwait(false)) {
			throw new PrerequisiteBlockedException($"Job node {request.JobNodeId}'s prerequisites are not satisfied.");
		}

		var previousAchievement = leafWork.Achievement;
		leafWork.Achievement = request.NewAchievement;
		leafWork.ChangedAt = SystemClock.Instance.GetCurrentInstant();
		leafWork.RowVersion += 1;

		AuditEventWriter.Add(
			context, request.Context.Actor, leafWork.ChangedAt, "set-achievement", "leaf_work", leafWork.JobNodeId.Value,
			request.Context.CorrelationId, request.Reason,
			new Dictionary<string, string?> { ["achievement"] = previousAchievement.ToString() },
			new Dictionary<string, string?> { ["achievement"] = leafWork.Achievement.ToString() });

		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException ex) {
			throw new ConcurrencyConflictException(
				$"Expected version {request.Version} for job node {request.JobNodeId} did not match its current version.", ex);
		}

		return ToResult(leafWork);
	}

	private Task<SqliteJobTrackDbContext> CreateOpenContextAsync(CancellationToken cancellationToken) =>
		SqliteDbContextFactory.CreateOpenContextAsync(connectionString, cancellationToken);

	private static async Task AuthorizeOrThrowAsync(
		SqliteJobTrackDbContext context, AppUserId actorId, JobNodeId nodeId, bool isReopening, CancellationToken cancellationToken)
	{
		var actorRoles = await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);
		var ancestorOwnerIds = await JobNodeHierarchyQueries.GetAncestorOwnerIdsAsync(context, nodeId.Value, cancellationToken)
			.ConfigureAwait(false);

		if (ancestorOwnerIds.Count == 0) {
			throw new EntityNotFoundException($"Job node {nodeId} does not exist.");
		}

		if (!AchievementAccessPolicy.CanSetAchievement(actorRoles, ancestorOwnerIds.Contains(actorId.Value), isReopening)) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not change achievement for job node {nodeId}.");
		}
	}

	private static async Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		SqliteJobTrackDbContext context, AppUserId actorId, CancellationToken cancellationToken)
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

	private static void CheckVersionOrThrow(long currentVersion, long expectedVersion)
	{
		if (currentVersion != expectedVersion) {
			throw new ConcurrencyConflictException(
				$"Expected version {expectedVersion} but the current version is {currentVersion}.");
		}
	}

	private static LeafWorkResult ToResult(LeafWorkEntity leafWork) => new() {
		JobNodeId = leafWork.JobNodeId,
		Achievement = leafWork.Achievement,
		PartialCriteria = leafWork.PartialCriteria,
		FullCriteria = leafWork.FullCriteria,
		ChangedAt = leafWork.ChangedAt,
		Version = leafWork.RowVersion,
	};
}
