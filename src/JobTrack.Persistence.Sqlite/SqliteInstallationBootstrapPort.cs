namespace JobTrack.Persistence.Sqlite;

using System.Data;
using Abstractions;
using Application;
using Application.Ports;
using Domain.Schedules;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared.Entities;

/// <summary>
///     SQLite implementation of <see cref="IInstallationBootstrapPort" /> (ADR 0005, ADR 0015). One
///     <see cref="SqliteJobTrackDbContext" />/connection/transaction per call; SQLite has no advisory
///     lock, so <see cref="IsolationLevel.Serializable" /> starts a <c>BEGIN IMMEDIATE</c> transaction
///     that takes the write lock immediately and serializes concurrent bootstrap attempts through
///     SQLite's single-writer model (matches <c>SqliteDeploymentLockStrategy</c>'s established use of
///     the same technique).
/// </summary>
internal sealed class SqliteInstallationBootstrapPort : IInstallationBootstrapPort
{
	private readonly IClock clock;
	private readonly string connectionString;

	/// <summary>Creates the port over the given SQLite connection string.</summary>
	public SqliteInstallationBootstrapPort(string connectionString, IClock clock)
	{
		this.connectionString = connectionString;
		this.clock = clock;
	}

	/// <inheritdoc />
	public async Task<BootstrapPersistenceResult> BootstrapAsync(
		BootstrapPersistenceRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = await SqliteDbContextFactory.CreateOpenContextAsync(connectionString, cancellationToken).ConfigureAwait(false);

		await using var transaction = await context.Database
			.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

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
		SqliteJobTrackDbContext context, AppUserId userId, string ianaTimeZone, Instant now, CancellationToken cancellationToken)
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
