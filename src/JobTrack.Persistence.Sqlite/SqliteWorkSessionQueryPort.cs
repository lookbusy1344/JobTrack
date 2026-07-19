namespace JobTrack.Persistence.Sqlite;

using Abstractions;
using Application;
using Application.Ports;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared;
using Shared.Entities;

/// <summary>
///     SQLite implementation of <see cref="IWorkSessionQueryPort" /> (plan §8.5 slice 4). One
///     <see cref="SqliteJobTrackDbContext" /> per call, read-only throughout.
/// </summary>
internal sealed class SqliteWorkSessionQueryPort : IWorkSessionQueryPort
{
	private readonly string connectionString;

	/// <summary>Creates the port over the given SQLite connection string.</summary>
	public SqliteWorkSessionQueryPort(string connectionString) => this.connectionString = connectionString;

	/// <inheritdoc />
	public async Task<WorkSessionQueryResult> GetSessionsAsync(
		AppUserId actorId, JobNodeId leafWorkId, AppUserId? workedByUserId,
		int offset = 0, int? limit = null, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();

		var actorRoles = await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);

		if (!await context.Set<JobNodeEntity>().AsNoTracking()
				.AnyAsync(n => n.Id == leafWorkId, cancellationToken).ConfigureAwait(false)) {
			throw new EntityNotFoundException($"Job node {leafWorkId} does not exist.");
		}

		var query = context.Set<WorkSessionEntity>().AsNoTracking()
			.Where(s => s.LeafWorkId == leafWorkId && (workedByUserId == null || s.WorkedByUserId == workedByUserId))
			.OrderByDescending(s => s.StartedAt).ThenByDescending(s => s.Id)
			.Skip(offset)
			.Select(s => new WorkSessionResult {
				Id = s.Id,
				LeafWorkId = s.LeafWorkId,
				WorkedByUserId = s.WorkedByUserId,
				StartedAt = s.StartedAt,
				FinishedAt = s.FinishedAt,
				ChangedAt = s.ChangedAt,
				Version = s.RowVersion,
			});
		var sessions = await (limit.HasValue ? query.Take(limit.Value) : query)
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);

		return new() { ActorRoles = actorRoles, Sessions = [.. sessions] };
	}

	/// <inheritdoc />
	public async Task<WorkSessionQueryResult> GetActiveSessionsAsync(
		AppUserId actorId, EquatableArray<JobNodeId> leafWorkIds, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();

		var actorRoles = await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);

		if (leafWorkIds.Count == 0) {
			return new() { ActorRoles = actorRoles, Sessions = [] };
		}

		var leafWorkIdList = leafWorkIds.ToList();
		var sessions = await context.Set<WorkSessionEntity>().AsNoTracking()
			.Where(s => s.FinishedAt == null && leafWorkIdList.Contains(s.LeafWorkId))
			.Select(s => new WorkSessionResult {
				Id = s.Id,
				LeafWorkId = s.LeafWorkId,
				WorkedByUserId = s.WorkedByUserId,
				StartedAt = s.StartedAt,
				FinishedAt = s.FinishedAt,
				ChangedAt = s.ChangedAt,
				Version = s.RowVersion,
			})
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);

		return new() { ActorRoles = actorRoles, Sessions = [.. sessions] };
	}

	private SqliteJobTrackDbContext CreateContext() => SqliteDbContextFactory.CreateContext(connectionString);

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
}
