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
///     PostgreSQL implementation of <see cref="IEmployeeQueryPort" /> (impl plan §7.3 slice 2). One
///     <see cref="PostgreSqlJobTrackDbContext" /> per call, read-only throughout.
/// </summary>
internal sealed class PostgreSqlEmployeeQueryPort : IEmployeeQueryPort
{
	private static readonly short[] WorkflowRoleIds = [
		(short)EmployeeRole.Administrator, (short)EmployeeRole.JobManager, (short)EmployeeRole.Worker,
	];

	private readonly IClock clock;

	private readonly NpgsqlDataSource dataSource;

	/// <summary>Creates the port over the given pooled <see cref="NpgsqlDataSource" />.</summary>
	public PostgreSqlEmployeeQueryPort(NpgsqlDataSource dataSource, IClock clock)
	{
		this.dataSource = dataSource;
		this.clock = clock;
	}

	/// <inheritdoc />
	public async Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		AppUserId actorId, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		return await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task<EmployeeProfileQueryResult> GetEmployeeProfileAsync(
		AppUserId actorId, AppUserId targetUserId, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();

		var actorRoles = await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);

		var target = await context.Set<AppUserEntity>().AsNoTracking()
						 .FirstOrDefaultAsync(u => u.Id == targetUserId, cancellationToken).ConfigureAwait(false)
					 ?? throw new EntityNotFoundException($"Employee {targetUserId} does not exist.");

		return new() {
			ActorRoles = actorRoles,
			Profile = new() {
				Id = target.Id,
				DisplayName = target.DisplayName,
				IanaTimeZone = target.IanaTimeZone,
				DefaultHourlyRate = target.DefaultHourlyRate,
				HomeNodeId = target.HomeNodeId,
				Version = target.RowVersion,
			},
		};
	}

	/// <inheritdoc />
	public async Task<EquatableArray<EmployeeDirectoryEntry>> GetEmployeeDirectoryAsync(CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();

		var rows = await (
			from iu in context.Set<IdentityUserEntity>().AsNoTracking()
			join ur in context.Set<IdentityUserRoleEntity>().AsNoTracking() on iu.Id equals ur.IdentityUserId
			join au in context.Set<AppUserEntity>().AsNoTracking() on iu.AppUserId equals au.Id
			where iu.IsEnabled && WorkflowRoleIds.Contains(ur.IdentityRoleId)
			select new { au.Id, au.DisplayName, iu.UserName }
		).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);

		return EquatableArray.CopyOf(
			rows.Select(row => new EmployeeDirectoryEntry { Id = row.Id, DisplayName = row.DisplayName, UserName = row.UserName })
				.OrderBy(entry => entry.DisplayName, StringComparer.Ordinal));
	}

	/// <inheritdoc />
	public async Task<EquatableArray<EmployeeDirectoryEntry>> GetAllEmployeesAsync(CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();

		var rows = await (
			from iu in context.Set<IdentityUserEntity>().AsNoTracking()
			join au in context.Set<AppUserEntity>().AsNoTracking() on iu.AppUserId equals au.Id
			select new { au.Id, au.DisplayName, iu.UserName }
		).ToListAsync(cancellationToken).ConfigureAwait(false);

		return EquatableArray.CopyOf(
			rows.Select(row => new EmployeeDirectoryEntry { Id = row.Id, DisplayName = row.DisplayName, UserName = row.UserName })
				.OrderBy(entry => entry.DisplayName, StringComparer.Ordinal));
	}

	/// <inheritdoc />
	public async Task<AccountStateQueryResult> GetAccountStateAsync(
		AppUserId actorId, AppUserId targetUserId, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();

		var actorRoles = await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);

		var target = await context.Set<IdentityUserEntity>().AsNoTracking()
						 .FirstOrDefaultAsync(iu => iu.AppUserId == targetUserId, cancellationToken).ConfigureAwait(false)
					 ?? throw new EntityNotFoundException($"Employee {targetUserId} does not exist.");

		var targetRoles = await GetRolesForIdentityUserAsync(context, target.Id, cancellationToken).ConfigureAwait(false);

		return new() {
			ActorRoles = actorRoles,
			AccountState = new() {
				Id = targetUserId,
				UserName = target.UserName,
				IsEnabled = target.IsEnabled,
				RequiresPasswordChange = target.RequiresPasswordChange,
				LockoutEnd = target.LockoutEnd,
				Roles = targetRoles,
			},
		};
	}

	private PostgreSqlJobTrackDbContext CreateContext()
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, o => o.UseNodaTime())
			.Options;

		return new(options);
	}

	private async Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		PostgreSqlJobTrackDbContext context, AppUserId actorId, CancellationToken cancellationToken)
	{
		var actorIdentityUser = await context.Set<IdentityUserEntity>().AsNoTracking()
									.FirstOrDefaultAsync(iu => iu.AppUserId == actorId, cancellationToken).ConfigureAwait(false)
								?? throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		ActorAccountState.EnsureMayAct(actorIdentityUser, actorId, clock.GetCurrentInstant());

		return await GetRolesForIdentityUserAsync(context, actorIdentityUser.Id, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<EquatableArray<EmployeeRole>> GetRolesForIdentityUserAsync(
		PostgreSqlJobTrackDbContext context, long identityUserId, CancellationToken cancellationToken)
	{
		var roles = await context.Set<IdentityUserRoleEntity>().AsNoTracking()
			.Where(ur => ur.IdentityUserId == identityUserId)
			.Select(ur => (EmployeeRole)ur.IdentityRoleId)
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);

		return [.. roles];
	}
}
