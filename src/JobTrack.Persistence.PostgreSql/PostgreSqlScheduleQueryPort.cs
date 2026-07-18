namespace JobTrack.Persistence.PostgreSql;

using Abstractions;
using Application;
using Application.Ports;
using Domain.Schedules;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Shared;
using Shared.Entities;

/// <summary>
///     PostgreSQL implementation of <see cref="IScheduleQueryPort" /> (plan §8.5 slice 6). One
///     <see cref="PostgreSqlJobTrackDbContext" /> per call, read-only throughout.
/// </summary>
internal sealed class PostgreSqlScheduleQueryPort : IScheduleQueryPort
{
	private readonly NpgsqlDataSource dataSource;

	/// <summary>Creates the port over the given pooled <see cref="NpgsqlDataSource" />.</summary>
	public PostgreSqlScheduleQueryPort(NpgsqlDataSource dataSource) => this.dataSource = dataSource;

	/// <inheritdoc />
	public async Task<ScheduleQueryResult> GetScheduleAsync(
		AppUserId actorId, AppUserId userId, CancellationToken cancellationToken = default)
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, o => o.UseNodaTime())
			.Options;
		await using var context = new PostgreSqlJobTrackDbContext(options);

		var actorRoles = await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);

		if (!await context.Set<AppUserEntity>().AsNoTracking()
				.AnyAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false)) {
			throw new EntityNotFoundException($"Employee {userId} does not exist.");
		}

		var versionEntities = await context.Set<ScheduleVersionEntity>().AsNoTracking()
			.Where(v => v.UserId == userId).ToListAsync(cancellationToken).ConfigureAwait(false);
		var versionIds = versionEntities.Select(v => v.Id).ToList();
		var intervalEntities = await context.Set<ScheduleIntervalEntity>().AsNoTracking()
			.Where(i => versionIds.Contains(i.ScheduleVersionId)).ToListAsync(cancellationToken).ConfigureAwait(false);
		var intervalsByVersion = intervalEntities.GroupBy(i => i.ScheduleVersionId).ToDictionary(g => g.Key, g => g.ToList());

		var versions = versionEntities.Select(version => {
			var weeklyIntervals = intervalsByVersion.GetValueOrDefault(version.Id, [])
				.Select(i => new WeeklyInterval(i.DayOfWeek, i.StartTime, i.EndTime));

			return new ScheduleVersionResult {
				Id = version.Id,
				UserId = version.UserId,
				Schedule = new(
					StoredTimeZoneResolver.Resolve(version.IanaTimeZone, $"Schedule version {version.Id}"),
					version.EffectiveStart, version.EffectiveEnd,
					EquatableArray.CopyOf(weeklyIntervals)),
				ChangedAt = version.ChangedAt,
				Version = version.RowVersion,
			};
		}).ToArray();

		var exceptionEntities = await context.Set<ScheduleExceptionEntity>().AsNoTracking()
			.Where(e => e.UserId == userId).ToListAsync(cancellationToken).ConfigureAwait(false);

		var exceptions = exceptionEntities.Select(exception => new ScheduleExceptionResult {
			Id = exception.Id,
			UserId = exception.UserId,
			Entry = new(
				(ScheduleExceptionEffect)exception.ScheduleExceptionEffectId,
				new(exception.StartedAt, exception.FinishedAt),
				exception.RateOverride),
			Reason = exception.Reason,
			CreatedBy = exception.CreatedBy,
			ChangedAt = exception.ChangedAt,
			Version = exception.RowVersion,
		}).ToArray();

		return new() { ActorRoles = actorRoles, Versions = [.. versions], Exceptions = [.. exceptions] };
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
}
