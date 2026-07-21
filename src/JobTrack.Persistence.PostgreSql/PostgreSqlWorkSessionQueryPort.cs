namespace JobTrack.Persistence.PostgreSql;

using Abstractions;
using Application;
using Application.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using Npgsql;
using Shared;
using Shared.Entities;

/// <summary>
///     PostgreSQL implementation of <see cref="IWorkSessionQueryPort" /> (plan §8.5 slice 4). One
///     <see cref="PostgreSqlJobTrackDbContext" /> per call, read-only throughout.
/// </summary>
internal sealed class PostgreSqlWorkSessionQueryPort : IWorkSessionQueryPort
{
	private readonly IReadOnlyList<IInterceptor> _interceptors;
	private readonly IClock clock;
	private readonly NpgsqlDataSource dataSource;

	/// <summary>Creates the port over the given pooled <see cref="NpgsqlDataSource" />.</summary>
	public PostgreSqlWorkSessionQueryPort(NpgsqlDataSource dataSource, IClock clock) : this(dataSource, clock, [])
	{
	}

	/// <summary>Test-only seam (Stage 4 efficiency guards) for attaching a command-count interceptor.</summary>
	internal PostgreSqlWorkSessionQueryPort(NpgsqlDataSource dataSource, IClock clock, IReadOnlyList<IInterceptor> interceptors)
	{
		this.dataSource = dataSource;
		this.clock = clock;
		_interceptors = interceptors;
	}

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

	/// <inheritdoc />
	public async Task<WorkSessionManageCapabilityQueryResult> GetManageCapabilitiesAsync(
		AppUserId actorId, EquatableArray<JobNodeId> leafWorkIds, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();

		var actorRoles = await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);

		if (leafWorkIds.Count == 0) {
			return new() { ActorRoles = actorRoles, ControlledLeafWorkIds = [] };
		}

		var leafWorkIdValues = leafWorkIds.Select(id => id.Value).ToArray();
		var controlledIds = await context.Database.SqlQuery<long>(
			$"""
			 SELECT controlled_leaf_id AS "Value"
			 FROM job_node_controlled_leaf_ids({actorId.Value}, {leafWorkIdValues})
			 """).ToListAsync(cancellationToken).ConfigureAwait(false);

		return new() { ActorRoles = actorRoles, ControlledLeafWorkIds = [.. controlledIds.Select(id => new JobNodeId(id))] };
	}

	private PostgreSqlJobTrackDbContext CreateContext()
	{
		var optionsBuilder = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, o => o.UseNodaTime());
		if (_interceptors.Count > 0) {
			optionsBuilder = optionsBuilder.AddInterceptors(_interceptors);
		}

		return new(optionsBuilder.Options);
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
