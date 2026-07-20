namespace JobTrack.Persistence.PostgreSql;

using Abstractions;
using Application;
using Application.Ports;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Shared;
using Shared.Entities;

/// <summary>
///     PostgreSQL implementation of <see cref="IRateQueryPort" /> (plan §8.5 slice 7). One
///     <see cref="PostgreSqlJobTrackDbContext" /> per call, read-only throughout.
/// </summary>
internal sealed class PostgreSqlRateQueryPort : IRateQueryPort
{
	private readonly IClock clock;
	private readonly NpgsqlDataSource dataSource;

	/// <summary>Creates the port over the given pooled <see cref="NpgsqlDataSource" />.</summary>
	public PostgreSqlRateQueryPort(NpgsqlDataSource dataSource, IClock clock)
	{
		this.dataSource = dataSource;
		this.clock = clock;
	}

	/// <inheritdoc />
	public async Task<RateQueryResult> GetRatesAsync(
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

		var userCostRateEntities = await context.Set<UserCostRateEntity>().AsNoTracking()
			.Where(r => r.UserId == userId).ToListAsync(cancellationToken).ConfigureAwait(false);

		var userCostRates = userCostRateEntities.Select(entity => new UserCostRateResult {
			Id = entity.Id,
			UserId = entity.UserId,
			Rate = new(entity.Rate, entity.EffectiveStart, entity.EffectiveEnd),
			ChangedAt = entity.ChangedAt,
			Version = entity.RowVersion,
		}).ToArray();

		var nodeRateOverrideEntities = await context.Set<NodeRateOverrideEntity>().AsNoTracking()
			.Where(o => o.UserId == userId).ToListAsync(cancellationToken).ConfigureAwait(false);

		var nodeRateOverrides = nodeRateOverrideEntities.Select(entity => new NodeRateOverrideResult {
			Id = entity.Id,
			UserId = entity.UserId,
			Override = new(entity.NodeId, entity.Rate, entity.EffectiveStart, entity.EffectiveEnd),
			ChangedAt = entity.ChangedAt,
			Version = entity.RowVersion,
		}).ToArray();

		return new() { ActorRoles = actorRoles, UserCostRates = [.. userCostRates], NodeRateOverrides = [.. nodeRateOverrides] };
	}

	private async Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		PostgreSqlJobTrackDbContext context, AppUserId actorId, CancellationToken cancellationToken)
	{
		var actorIdentityUser = await context.Set<IdentityUserEntity>().AsNoTracking()
									.FirstOrDefaultAsync(iu => iu.AppUserId == actorId, cancellationToken).ConfigureAwait(false)
								?? throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		ActorAccountState.EnsureMayAct(actorIdentityUser, actorId, clock.GetCurrentInstant());

		var roles = await context.Set<IdentityUserRoleEntity>().AsNoTracking()
			.Where(ur => ur.IdentityUserId == actorIdentityUser.Id)
			.Select(ur => (EmployeeRole)ur.IdentityRoleId)
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);

		return [.. roles];
	}
}
