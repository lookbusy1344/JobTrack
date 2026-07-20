namespace JobTrack.Persistence.PostgreSql;

using Abstractions;
using Application;
using Application.Ports;
using Domain.Authorization;
using Domain.Hierarchy;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Shared;
using Shared.Entities;

/// <summary>
///     PostgreSQL implementation of <see cref="IAchievementCommandPort" /> (impl plan §7.3 slice 7:
///     change achievement subject to prerequisite gates, ADR 0001). One
///     <see cref="PostgreSqlJobTrackDbContext" />/connection/transaction per call, reloading the actor's
///     current roles and subtree ownership and applying <see cref="AchievementAccessPolicy" /> and
///     <see cref="AchievementTransitions" /> itself, then -- for a transition into a completed state --
///     rechecking prerequisite readiness, the same shape as
///     <see cref="PostgreSqlWorkSessionCommandPort.StartSessionAsync" />. Achievement is not one of ADR
///     0012's lock domains: the state machine is enforced purely application-side against a single
///     tracked <c>leaf_work</c> row guarded by its own concurrency token, not by a database trigger.
/// </summary>
internal sealed class PostgreSqlAchievementCommandPort : IAchievementCommandPort
{
	private readonly IClock clock;
	private readonly NpgsqlDataSource dataSource;

	/// <summary>Creates the port over the given pooled <see cref="NpgsqlDataSource" />.</summary>
	public PostgreSqlAchievementCommandPort(NpgsqlDataSource dataSource, IClock clock)
	{
		this.dataSource = dataSource;
		this.clock = clock;
	}

	/// <inheritdoc />
	public async Task<LeafWorkResult> SetAchievementAsync(SetAchievementRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		var leafWork = await context.Set<LeafWorkEntity>()
						   .FirstOrDefaultAsync(lw => lw.JobNodeId == request.JobNodeId, cancellationToken).ConfigureAwait(false)
					   ?? throw new EntityNotFoundException($"Job node {request.JobNodeId} has no LeafWork attached.");

		var now = clock.GetCurrentInstant();
		var isReopening = AchievementTransitions.IsReopening(leafWork.Achievement, request.NewAchievement);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.JobNodeId, isReopening, now, cancellationToken).ConfigureAwait(false);
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
		leafWork.ChangedAt = now;
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

	private PostgreSqlJobTrackDbContext CreateContext()
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, o => o.UseNodaTime())
			.Options;

		return new(options);
	}

	private static async Task AuthorizeOrThrowAsync(
		PostgreSqlJobTrackDbContext context, AppUserId actorId, JobNodeId nodeId, bool isReopening, Instant now,
		CancellationToken cancellationToken)
	{
		var actorRoles = await GetActorRolesAsync(context, actorId, now, cancellationToken).ConfigureAwait(false);
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
		PostgreSqlJobTrackDbContext context, AppUserId actorId, Instant now, CancellationToken cancellationToken)
	{
		var actorIdentityUser = await context.Set<IdentityUserEntity>().AsNoTracking()
									.FirstOrDefaultAsync(iu => iu.AppUserId == actorId, cancellationToken).ConfigureAwait(false)
								?? throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		ActorAccountState.EnsureMayAct(actorIdentityUser, actorId, now);

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
