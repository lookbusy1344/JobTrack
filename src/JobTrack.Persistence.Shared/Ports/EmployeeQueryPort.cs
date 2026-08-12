namespace JobTrack.Persistence.Shared.Ports;

using Abstractions;
using Application;
using Application.Ports;
using Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

/// <summary>
///     The provider-neutral body of <see cref="IEmployeeQueryPort" /> (impl plan §7.3 slice 2). One
///     <see cref="DbContext" /> per call, read-only throughout.
/// </summary>
internal sealed class EmployeeQueryPort(IProviderReadOperations provider, IClock clock) : IEmployeeQueryPort
{
	// IReadOnlyList rather than short[]: readonly freezes the reference, not the elements, so an
	// array field reads as a constant table while any member could rewrite it. A ReadOnlySpan
	// property is not available here -- this is captured by an EF expression tree, which cannot
	// hold a ref struct.
	private static readonly IReadOnlyList<short> WorkflowRoleIds = [
		(short)EmployeeRole.Administrator, (short)EmployeeRole.JobManager, (short)EmployeeRole.Worker,
	];

	/// <inheritdoc />
	public async Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		AppUserId actorId, CancellationToken cancellationToken = default)
	{
		await using var context = provider.CreateContext();
		return await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task<EmployeeProfileQueryResult> GetEmployeeProfileAsync(
		AppUserId actorId, AppUserId targetUserId, CancellationToken cancellationToken = default)
	{
		await using var context = provider.CreateContext();

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
		await using var context = provider.CreateContext();

		var rows = await (
				from iu in context.Set<IdentityUserEntity>().AsNoTracking()
				join ur in context.Set<IdentityUserRoleEntity>().AsNoTracking() on iu.Id equals ur.IdentityUserId
				join au in context.Set<AppUserEntity>().AsNoTracking() on iu.AppUserId equals au.Id
				where iu.IsEnabled
					  && WorkflowRoleIds.Contains(ur.IdentityRoleId)
					  && !context.Set<IdentityUserRoleEntity>().Any(requesterRole => requesterRole.IdentityUserId == iu.Id
																					 && requesterRole.IdentityRoleId == (short)EmployeeRole.Requester)
				select new
				{
					au.Id,
					au.DisplayName,
					iu.UserName,
				}
			).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);

		return EquatableArray.CopyOf(
			rows.Select(row => new EmployeeDirectoryEntry {
				Id = row.Id,
				DisplayName = row.DisplayName,
				UserName = row.UserName,
			})
				.OrderBy(entry => entry.DisplayName, StringComparer.Ordinal));
	}

	/// <inheritdoc />
	public async Task<EquatableArray<EmployeeDirectoryEntry>> GetAllEmployeesAsync(CancellationToken cancellationToken = default)
	{
		await using var context = provider.CreateContext();

		var rows = await (
				from iu in context.Set<IdentityUserEntity>().AsNoTracking()
				join au in context.Set<AppUserEntity>().AsNoTracking() on iu.AppUserId equals au.Id
				select new
				{
					au.Id,
					au.DisplayName,
					iu.UserName,
				}
			).ToListAsync(cancellationToken).ConfigureAwait(false);

		return EquatableArray.CopyOf(
			rows.Select(row => new EmployeeDirectoryEntry {
				Id = row.Id,
				DisplayName = row.DisplayName,
				UserName = row.UserName,
			})
				.OrderBy(entry => entry.DisplayName, StringComparer.Ordinal));
	}

	/// <inheritdoc />
	public async Task<AccountStateQueryResult> GetAccountStateAsync(
		AppUserId actorId, AppUserId targetUserId, CancellationToken cancellationToken = default)
	{
		await using var context = provider.CreateContext();

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

	private async Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		DbContext context, AppUserId actorId, CancellationToken cancellationToken)
	{
		var actorIdentityUser = await context.Set<IdentityUserEntity>().AsNoTracking()
											 .FirstOrDefaultAsync(iu => iu.AppUserId == actorId, cancellationToken).ConfigureAwait(false)
								?? throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		ActorAccountState.EnsureMayAct(actorIdentityUser, actorId, clock.GetCurrentInstant());

		return await GetRolesForIdentityUserAsync(context, actorIdentityUser.Id, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<EquatableArray<EmployeeRole>> GetRolesForIdentityUserAsync(
		DbContext context, long identityUserId, CancellationToken cancellationToken)
	{
		var roles = await context.Set<IdentityUserRoleEntity>().AsNoTracking()
								 .Where(ur => ur.IdentityUserId == identityUserId)
								 .Select(ur => (EmployeeRole)ur.IdentityRoleId)
								 .ToArrayAsync(cancellationToken).ConfigureAwait(false);

		return [.. roles];
	}
}
