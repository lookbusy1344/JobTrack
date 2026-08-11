namespace JobTrack.Persistence.PostgreSql;

using Abstractions;
using Application;
using Application.Ports;
using Domain.Schedules;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Shared.Entities;

/// <summary>
///     PostgreSQL implementation of <see cref="IInstallationBootstrapPort" /> (ADR 0005, ADR 0012,
///     ADR 0015). One <see cref="PostgreSqlJobTrackDbContext" />/connection/transaction per call.
/// </summary>
internal sealed class PostgreSqlInstallationBootstrapPort : IInstallationBootstrapPort
{
	private readonly IClock clock;
	private readonly NpgsqlDataSource dataSource;

	/// <summary>Creates the port over the given pooled <see cref="NpgsqlDataSource" />.</summary>
	public PostgreSqlInstallationBootstrapPort(NpgsqlDataSource dataSource, IClock clock)
	{
		this.dataSource = dataSource;
		this.clock = clock;
	}

	/// <inheritdoc />
	public async Task<BootstrapPersistenceResult> BootstrapAsync(
		BootstrapPersistenceRequest request, CancellationToken cancellationToken = default)
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, o => o.UseNodaTime())
			.Options;

		await using var context = new PostgreSqlJobTrackDbContext(options);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// ADR 0012/0015: acquire the fixed bootstrap advisory lock before any read of the marker.
		_ = await context.Database.ExecuteSqlInterpolatedAsync(
			$"SELECT pg_advisory_xact_lock(hashtext({PostgreSqlLockKeys.Bootstrap}));", cancellationToken).ConfigureAwait(false);

		if (await context.Set<InitialisedMarkerEntity>().AnyAsync(cancellationToken).ConfigureAwait(false)) {
			throw new InvariantViolationException(
				"installation-already-initialised", "The installation has already been bootstrapped (ADR 0015).");
		}

		var now = clock.GetCurrentInstant();
		var canonicalZone = ScheduleZoneId.Resolve(request.IanaTimeZone);

		var administrator = new AppUserEntity {
			Id = default,
			DisplayName = request.DisplayName,
			IanaTimeZone = canonicalZone.Id,
			DefaultHourlyRate = request.DefaultHourlyRate ?? EmployeeProvisioningDefaults.HourlyRate,
			RowVersion = 1,
		};
		_ = context.Add(administrator);

		// Save the administrator alone first: identity_user/job_node reference administrator.Id
		// by value (no navigation properties, per the persistence entities' navigation-free
		// design), so their FK values must be assigned after the database generates it, not before.
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		await AddDefaultScheduleAsync(context, administrator.Id, canonicalZone.Id, now, cancellationToken).ConfigureAwait(false);

		var identityUser = new IdentityUserEntity {
			AppUserId = administrator.Id,
			UserName = request.UserName,
			NormalizedUserName = request.UserName.ToUpperInvariant(),
			PasswordHash = request.PasswordHash,
			SecurityStamp = request.SecurityStamp,
			ConcurrencyStamp = Guid.NewGuid().ToString("N"),
			RequiresPasswordChange = true,
			IsEnabled = true,
			LockoutEnabled = true,
			AccessFailedCount = 0,
		};
		_ = context.Add(identityUser);

		// Save identityUser alone next: the role-membership row below is FK'd to identity_user.id
		// (not app_user.id), so it needs that generated id assigned before it can be constructed.
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		_ = context.Add(new IdentityUserRoleEntity { IdentityUserId = identityUser.Id, IdentityRoleId = (short)EmployeeRole.Administrator });

		var root = new JobNodeEntity {
			Id = default,
			ParentId = null,
			Description = "Root",
			PostedByUserId = administrator.Id,
			OwnerUserId = administrator.Id,
			Priority = Priority.Medium,
			PostedAt = now,
			RowVersion = 1,
		};
		_ = context.Add(root);

		_ = context.Add(new InitialisedMarkerEntity { Id = 1, InitialisedAt = now });

		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		return new() {
			AdministratorId = administrator.Id,
			AdministratorVersion = administrator.RowVersion,
			RootJobNodeId = root.Id,
			RootVersion = root.RowVersion,
			InitializedAt = now,
		};
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
