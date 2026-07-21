namespace JobTrack.Persistence.Sqlite;

using System.Data;
using Abstractions;
using Application;
using Application.Ports;
using Domain.Authorization;
using Domain.Hierarchy;
using Microsoft.Data.Sqlite;
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
///     <see cref="SqliteWorkSessionCommandPort" />'s established use of the same technique). The
///     transition state machine itself is enforced purely application-side against a single tracked
///     <c>leaf_work</c> row guarded by its own concurrency token, not by a trigger. The one exception
///     is ADR 0044: a transition into a terminal value additionally rechecks (and, under a race, is
///     backstopped by schema version 0007's immediate trigger) that no <c>work_session</c> on the leaf
///     is still active.
/// </summary>
internal sealed class SqliteAchievementCommandPort : IAchievementCommandPort
{
	/// <summary>
	///     ADR 0044: the literal message <c>leaf_work_no_active_sessions_on_terminal_achievement</c>
	///     (schema version 0007) raises via <c>RAISE(ABORT, ...)</c>.
	/// </summary>
	private const string ActiveSessionsMessage = "leaf-closure-active-sessions";

	private readonly IClock clock;
	private readonly string connectionString;

	/// <summary>Creates the port over the given SQLite connection string.</summary>
	public SqliteAchievementCommandPort(string connectionString, IClock clock)
	{
		this.connectionString = connectionString;
		this.clock = clock;
	}

	/// <inheritdoc />
	public async Task<LeafWorkResult> SetAchievementAsync(SetAchievementRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await CreateOpenContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

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

		if (AchievementTransitions.IsCompletedState(request.NewAchievement)) {
			if (!await LeafReadiness.IsReadyAsync(context, request.JobNodeId, cancellationToken).ConfigureAwait(false)) {
				throw new PrerequisiteBlockedException($"Job node {request.JobNodeId}'s prerequisites are not satisfied.");
			}

			if (await LeafSessionClosure.HasActiveSessionAsync(context, request.JobNodeId, cancellationToken).ConfigureAwait(false)) {
				throw new InvariantViolationException(
					"leaf-closure-active-sessions", "This leaf cannot transition to a terminal achievement while a session is active.");
			}
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
		catch (Exception ex) when (FindActiveSessionsViolation(ex) is not null) {
			throw new InvariantViolationException(
				"leaf-closure-active-sessions", "This leaf cannot transition to a terminal achievement while a session is active.", ex);
		}

		return ToResult(leafWork);
	}

	private static SqliteException? FindActiveSessionsViolation(Exception? ex) =>
		ex switch {
			null => null,
			SqliteException sqlite when sqlite.Message.Contains(ActiveSessionsMessage, StringComparison.Ordinal) => sqlite,
			_ => FindActiveSessionsViolation(ex.InnerException),
		};

	private Task<SqliteJobTrackDbContext> CreateOpenContextAsync(CancellationToken cancellationToken) =>
		SqliteDbContextFactory.CreateOpenContextAsync(connectionString, cancellationToken);

	private static async Task AuthorizeOrThrowAsync(
		SqliteJobTrackDbContext context, AppUserId actorId, JobNodeId nodeId, bool isReopening, Instant now,
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
		SqliteJobTrackDbContext context, AppUserId actorId, Instant now, CancellationToken cancellationToken)
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
