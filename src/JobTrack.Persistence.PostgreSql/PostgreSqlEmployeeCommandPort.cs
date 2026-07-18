namespace JobTrack.Persistence.PostgreSql;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application;
using Application.Ports;
using Domain.Authorization;
using Domain.Schedules;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Shared;
using Shared.Entities;

/// <summary>
///     PostgreSQL implementation of <see cref="IEmployeeCommandPort" /> (plan §8.3: employee role
///     assignment, account enable/disable, password reset). One
///     <see cref="PostgreSqlJobTrackDbContext" />/connection/transaction per call, reloading the actor's
///     current roles and applying <see cref="EmployeeAccessPolicy" /> itself before writing. Role
///     assignment/revocation and account enable/disable are idempotent (schema version 0002's
///     <c>identity_user_role</c> primary key already prevents a duplicate grant, and enable/disable
///     compares against current state), so a no-effect call is a deliberate no-op rather than
///     surfacing a constraint violation, writing a spurious audit event, or rotating the security stamp
///     for nothing.
/// </summary>
internal sealed class PostgreSqlEmployeeCommandPort : IEmployeeCommandPort
{
	private readonly NpgsqlDataSource dataSource;

	/// <summary>Creates the port over the given pooled <see cref="NpgsqlDataSource" />.</summary>
	public PostgreSqlEmployeeCommandPort(NpgsqlDataSource dataSource) => this.dataSource = dataSource;

	/// <inheritdoc />
	public async Task<AccountStateResult> CreateEmployeeAsync(
		CreateEmployeePersistenceRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await AuthorizeAccountsOrThrowAsync(context, request.Context.Actor, cancellationToken).ConfigureAwait(false);

		var now = SystemClock.Instance.GetCurrentInstant();
		var canonicalZone = ScheduleZoneId.Resolve(request.IanaTimeZone);
		var appUser = new AppUserEntity {
			Id = default,
			DisplayName = request.DisplayName,
			IanaTimeZone = canonicalZone.Id,
			DefaultHourlyRate = request.DefaultHourlyRate ?? EmployeeProvisioningDefaults.HourlyRate,
			RowVersion = 1,
		};
		_ = context.Add(appUser);
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		await AddDefaultScheduleAsync(context, appUser.Id, canonicalZone.Id, now, cancellationToken).ConfigureAwait(false);

		var identityUser = new IdentityUserEntity {
			AppUserId = appUser.Id,
			UserName = request.UserName,
			NormalizedUserName = request.UserName.ToUpperInvariant(),
			PasswordHash = request.PasswordHash,
			SecurityStamp = Guid.NewGuid().ToString("N"),
			ConcurrencyStamp = Guid.NewGuid().ToString("N"),
			RequiresPasswordChange = true,
			IsEnabled = true,
			LockoutEnabled = true,
			AccessFailedCount = 0,
		};
		_ = context.Add(identityUser);

		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is DbUpdateException or DbException) {
			throw new InvariantViolationException(
				"employee-username-already-taken", $"Username '{request.UserName}' is already taken.", ex);
		}

		_ = context.Add(new IdentityUserRoleEntity { IdentityUserId = identityUser.Id, IdentityRoleId = (short)request.Role });

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "create-employee", "identity_user", identityUser.Id,
			request.Context.CorrelationId, null, null,
			new Dictionary<string, string?> { ["user_name"] = request.UserName, ["role"] = request.Role.ToString() });

		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		return new() {
			Id = appUser.Id,
			UserName = identityUser.UserName,
			IsEnabled = true,
			RequiresPasswordChange = true,
			LockoutEnd = null,
			Roles = [request.Role],
		};
	}

	/// <inheritdoc />
	public async Task<EmployeeRolesResult> AssignRoleAsync(
		AssignEmployeeRoleRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await AuthorizeRolesOrThrowAsync(context, request.Context.Actor, cancellationToken).ConfigureAwait(false);
		var (targetIdentityUserId, currentRoles) =
			await LoadTargetAsync(context, request.TargetUserId, cancellationToken).ConfigureAwait(false);

		if (currentRoles.Contains(request.Role)) {
			return new() { UserId = request.TargetUserId, Roles = currentRoles };
		}

		var now = SystemClock.Instance.GetCurrentInstant();
		_ = context.Add(new IdentityUserRoleEntity { IdentityUserId = targetIdentityUserId, IdentityRoleId = (short)request.Role });

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "assign-employee-role", "identity_user_role", targetIdentityUserId,
			request.Context.CorrelationId, null, null,
			new Dictionary<string, string?> { ["role"] = request.Role.ToString() });

		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await RotateSecurityStampAsync(context, targetIdentityUserId, cancellationToken).ConfigureAwait(false);
		_ = await PersonalAccessTokenRevocation.RevokeAllForUserAsync(context, request.TargetUserId, now, cancellationToken)
			.ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		return new() { UserId = request.TargetUserId, Roles = [.. currentRoles, request.Role] };
	}

	/// <inheritdoc />
	public async Task<EmployeeRolesResult> RevokeRoleAsync(
		RevokeEmployeeRoleRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await AuthorizeRolesOrThrowAsync(context, request.Context.Actor, cancellationToken).ConfigureAwait(false);
		var (targetIdentityUserId, currentRoles) =
			await LoadTargetAsync(context, request.TargetUserId, cancellationToken).ConfigureAwait(false);

		if (!currentRoles.Contains(request.Role)) {
			return new() { UserId = request.TargetUserId, Roles = currentRoles };
		}

		var now = SystemClock.Instance.GetCurrentInstant();
		var affected = await context.Database.ExecuteSqlInterpolatedAsync(
			$"DELETE FROM identity_user_role WHERE identity_user_id = {targetIdentityUserId} AND identity_role_id = {(short)request.Role};",
			cancellationToken).ConfigureAwait(false);

		if (affected > 0) {
			AuditEventWriter.Add(
				context, request.Context.Actor, now, "revoke-employee-role", "identity_user_role", targetIdentityUserId,
				request.Context.CorrelationId, null, null,
				new Dictionary<string, string?> { ["role"] = request.Role.ToString() });
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await RotateSecurityStampAsync(context, targetIdentityUserId, cancellationToken).ConfigureAwait(false);
			_ = await PersonalAccessTokenRevocation.RevokeAllForUserAsync(context, request.TargetUserId, now, cancellationToken)
				.ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		return new() { UserId = request.TargetUserId, Roles = [.. currentRoles.Where(role => role != request.Role)] };
	}

	/// <inheritdoc />
	public async Task<AccountStateResult> SetEnabledAsync(
		SetEmployeeEnabledRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await AuthorizeAccountsOrThrowAsync(context, request.Context.Actor, cancellationToken).ConfigureAwait(false);
		var target = await context.Set<IdentityUserEntity>()
						 .FirstOrDefaultAsync(iu => iu.AppUserId == request.TargetUserId, cancellationToken).ConfigureAwait(false)
					 ?? throw new EntityNotFoundException($"Employee {request.TargetUserId} does not exist.");

		if (target.IsEnabled == request.Enabled) {
			var currentRoles = await GetRolesForIdentityUserAsync(context, target.Id, cancellationToken).ConfigureAwait(false);
			return BuildAccountStateResult(request.TargetUserId, target, currentRoles);
		}

		var now = SystemClock.Instance.GetCurrentInstant();
		target.IsEnabled = request.Enabled;
		target.SecurityStamp = Guid.NewGuid().ToString("N");

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "set-employee-enabled", "identity_user", target.Id,
			request.Context.CorrelationId, null, null,
			new Dictionary<string, string?> { ["is_enabled"] = request.Enabled.ToString() });

		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		if (!request.Enabled) {
			_ = await PersonalAccessTokenRevocation.RevokeAllForUserAsync(context, request.TargetUserId, now, cancellationToken)
				.ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		var roles = await GetRolesForIdentityUserAsync(context, target.Id, cancellationToken).ConfigureAwait(false);
		return BuildAccountStateResult(request.TargetUserId, target, roles);
	}

	/// <inheritdoc />
	public async Task<EmployeeProfileResult> SetDefaultHourlyRateAsync(
		SetEmployeeDefaultHourlyRateRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await AuthorizeAccountsOrThrowAsync(context, request.Context.Actor, cancellationToken).ConfigureAwait(false);
		var target = await context.Set<AppUserEntity>()
						 .FirstOrDefaultAsync(u => u.Id == request.TargetUserId, cancellationToken).ConfigureAwait(false)
					 ?? throw new EntityNotFoundException($"Employee {request.TargetUserId} does not exist.");
		_ = await context.Set<IdentityUserEntity>().AsNoTracking()
				.FirstOrDefaultAsync(iu => iu.AppUserId == request.TargetUserId, cancellationToken).ConfigureAwait(false)
			?? throw new EntityNotFoundException($"Employee {request.TargetUserId} does not exist.");

		var now = SystemClock.Instance.GetCurrentInstant();
		target.DefaultHourlyRate = request.DefaultHourlyRate;

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "set-employee-default-hourly-rate", "app_user", target.Id.Value,
			request.Context.CorrelationId, null, null,
			new Dictionary<string, string?> {
				["default_hourly_rate"] = request.DefaultHourlyRate.AmountPerHour.ToString(CultureInfo.InvariantCulture),
			});

		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		return new() {
			Id = target.Id,
			DisplayName = target.DisplayName,
			IanaTimeZone = target.IanaTimeZone,
			DefaultHourlyRate = target.DefaultHourlyRate,
			HomeNodeId = target.HomeNodeId,
			Version = target.RowVersion,
		};
	}

	/// <inheritdoc />
	public async Task<AccountStateResult> ResetPasswordAsync(
		ResetEmployeePasswordPersistenceRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await AuthorizeAccountsOrThrowAsync(context, request.Context.Actor, cancellationToken).ConfigureAwait(false);
		var target = await context.Set<IdentityUserEntity>()
						 .FirstOrDefaultAsync(iu => iu.AppUserId == request.TargetUserId, cancellationToken).ConfigureAwait(false)
					 ?? throw new EntityNotFoundException($"Employee {request.TargetUserId} does not exist.");

		var now = SystemClock.Instance.GetCurrentInstant();
		target.PasswordHash = request.PasswordHash;
		target.SecurityStamp = Guid.NewGuid().ToString("N");
		target.RequiresPasswordChange = true;

		// No password material in the audit payload (spec §16).
		AuditEventWriter.Add(
			context, request.Context.Actor, now, "reset-employee-password", "identity_user", target.Id,
			request.Context.CorrelationId, null, null, null);

		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		_ = await PersonalAccessTokenRevocation.RevokeAllForUserAsync(context, request.TargetUserId, now, cancellationToken)
			.ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		var roles = await GetRolesForIdentityUserAsync(context, target.Id, cancellationToken).ConfigureAwait(false);
		return BuildAccountStateResult(request.TargetUserId, target, roles);
	}

	/// <inheritdoc />
	public async Task<AccountStateResult> ResetTwoFactorAsync(
		ResetEmployeeTwoFactorRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await AuthorizeAccountsOrThrowAsync(context, request.Context.Actor, cancellationToken).ConfigureAwait(false);
		var target = await context.Set<IdentityUserEntity>()
						 .FirstOrDefaultAsync(iu => iu.AppUserId == request.TargetUserId, cancellationToken).ConfigureAwait(false)
					 ?? throw new EntityNotFoundException($"Employee {request.TargetUserId} does not exist.");

		var now = SystemClock.Instance.GetCurrentInstant();
		target.TwoFactorEnabled = false;
		target.AuthenticatorKeyProtected = null;
		target.TwoFactorEnabledAt = null;
		target.SecurityStamp = Guid.NewGuid().ToString("N");

		// No key material in the audit payload (spec §16).
		AuditEventWriter.Add(
			context, request.Context.Actor, now, "reset-employee-two-factor", "identity_user", target.Id,
			request.Context.CorrelationId, null, null, null);

		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		_ = await PersonalAccessTokenRevocation.RevokeAllForUserAsync(context, request.TargetUserId, now, cancellationToken)
			.ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		var roles = await GetRolesForIdentityUserAsync(context, target.Id, cancellationToken).ConfigureAwait(false);
		return BuildAccountStateResult(request.TargetUserId, target, roles);
	}

	/// <inheritdoc />
	public async Task<EmployeeProfileResult> SetHomeNodeAsync(
		SetHomeNodeRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();

		_ = await LoadActorAsync(context, request.Context.Actor, cancellationToken).ConfigureAwait(false);
		var actorAppUser = await context.Set<AppUserEntity>().AsNoTracking()
							   .FirstOrDefaultAsync(u => u.Id == request.Context.Actor, cancellationToken).ConfigureAwait(false)
						   ?? throw new EntityNotFoundException($"Employee {request.Context.Actor} does not exist.");

		if (request.NodeId is { } nodeId) {
			await EnsureNodeIsNotLeafAsync(context, nodeId, cancellationToken).ConfigureAwait(false);
		}

		_ = await context.Set<AppUserEntity>()
			.Where(u => u.Id == request.Context.Actor)
			.ExecuteUpdateAsync(setters => setters.SetProperty(u => u.HomeNodeId, request.NodeId), cancellationToken)
			.ConfigureAwait(false);

		return new() {
			Id = actorAppUser.Id,
			DisplayName = actorAppUser.DisplayName,
			IanaTimeZone = actorAppUser.IanaTimeZone,
			DefaultHourlyRate = actorAppUser.DefaultHourlyRate,
			HomeNodeId = request.NodeId,
			Version = actorAppUser.RowVersion,
		};
	}

	private static async Task EnsureNodeIsNotLeafAsync(
		PostgreSqlJobTrackDbContext context, JobNodeId nodeId, CancellationToken cancellationToken)
	{
		var node = await context.Set<JobNodeEntity>().AsNoTracking()
					   .FirstOrDefaultAsync(n => n.Id == nodeId, cancellationToken).ConfigureAwait(false)
				   ?? throw new EntityNotFoundException($"Job node {nodeId} does not exist.");

		var hasChildren = await context.Set<JobNodeEntity>().AsNoTracking()
			.AnyAsync(c => c.ParentId == nodeId, cancellationToken).ConfigureAwait(false);

		if (JobNodeStructuralResults.DeriveKind(node.ParentId, hasChildren) == NodeKind.Leaf) {
			throw new InvariantViolationException(
				"home-node-must-not-be-leaf", $"Job node {nodeId} is a leaf and cannot be set as a home node.");
		}
	}

	private PostgreSqlJobTrackDbContext CreateContext()
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, o => o.UseNodaTime())
			.Options;

		return new(options);
	}

	private static AccountStateResult BuildAccountStateResult(
		AppUserId targetUserId, IdentityUserEntity target, EquatableArray<EmployeeRole> roles) =>
		new() {
			Id = targetUserId,
			UserName = target.UserName,
			IsEnabled = target.IsEnabled,
			RequiresPasswordChange = target.RequiresPasswordChange,
			LockoutEnd = target.LockoutEnd,
			Roles = roles,
		};

	private static async Task RotateSecurityStampAsync(
		PostgreSqlJobTrackDbContext context, long identityUserId, CancellationToken cancellationToken)
	{
		var newStamp = Guid.NewGuid().ToString("N");
		_ = await context.Database.ExecuteSqlInterpolatedAsync(
			$"UPDATE identity_user SET security_stamp = {newStamp} WHERE id = {identityUserId};",
			cancellationToken).ConfigureAwait(false);
	}

	private static async Task<(long TargetIdentityUserId, EquatableArray<EmployeeRole> Roles)> LoadTargetAsync(
		PostgreSqlJobTrackDbContext context, AppUserId targetUserId, CancellationToken cancellationToken)
	{
		var targetIdentityUser = await context.Set<IdentityUserEntity>().AsNoTracking()
									 .FirstOrDefaultAsync(iu => iu.AppUserId == targetUserId, cancellationToken).ConfigureAwait(false)
								 ?? throw new EntityNotFoundException($"Employee {targetUserId} does not exist.");

		var roles = await GetRolesForIdentityUserAsync(context, targetIdentityUser.Id, cancellationToken).ConfigureAwait(false);

		return (targetIdentityUser.Id, roles);
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

	private static async Task<IdentityUserEntity> LoadActorAsync(
		PostgreSqlJobTrackDbContext context, AppUserId actorId, CancellationToken cancellationToken)
	{
		var actorIdentityUser = await context.Set<IdentityUserEntity>().AsNoTracking()
									.FirstOrDefaultAsync(iu => iu.AppUserId == actorId, cancellationToken).ConfigureAwait(false)
								?? throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		ActorAccountState.EnsureMayAct(actorIdentityUser, actorId, SystemClock.Instance.GetCurrentInstant());

		return actorIdentityUser;
	}

	private static async Task AuthorizeRolesOrThrowAsync(
		PostgreSqlJobTrackDbContext context, AppUserId actorId, CancellationToken cancellationToken)
	{
		var actorIdentityUser = await LoadActorAsync(context, actorId, cancellationToken).ConfigureAwait(false);
		var actorRoles = await GetRolesForIdentityUserAsync(context, actorIdentityUser.Id, cancellationToken).ConfigureAwait(false);

		if (!EmployeeAccessPolicy.CanManageRoles(actorRoles)) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not manage role assignments.");
		}
	}

	private static async Task AuthorizeAccountsOrThrowAsync(
		PostgreSqlJobTrackDbContext context, AppUserId actorId, CancellationToken cancellationToken)
	{
		var actorIdentityUser = await LoadActorAsync(context, actorId, cancellationToken).ConfigureAwait(false);
		var actorRoles = await GetRolesForIdentityUserAsync(context, actorIdentityUser.Id, cancellationToken).ConfigureAwait(false);

		if (!EmployeeAccessPolicy.CanManageAccounts(actorRoles)) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not manage employee accounts.");
		}
	}

	private static async Task AddDefaultScheduleAsync(
		PostgreSqlJobTrackDbContext context, AppUserId userId, string ianaTimeZone, Instant now, CancellationToken cancellationToken)
	{
		var schedule = EmployeeProvisioningDefaults.CreateSchedule(ianaTimeZone);
		var version = new ScheduleVersionEntity {
			Id = default,
			UserId = userId,
			EffectiveStart = schedule.EffectiveStart,
			EffectiveEnd = schedule.EffectiveEnd,
			IanaTimeZone = schedule.Zone.Id,
			ChangedAt = now,
			RowVersion = 1,
		};
		_ = context.Add(version);
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		foreach (var interval in schedule.WeeklyIntervals) {
			_ = context.Add(new ScheduleIntervalEntity {
				ScheduleVersionId = version.Id,
				DayOfWeek = interval.Day,
				StartTime = interval.Start,
				EndTime = interval.End,
				CrossesMidnight = interval.CrossesMidnight,
			});
		}
	}
}
