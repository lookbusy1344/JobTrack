namespace JobTrack.Persistence.PostgreSql;

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
///     PostgreSQL implementation of <see cref="IScheduleCommandPort" /> (impl plan §7.3 slice 8: add
///     schedule versions and exceptions). One <see cref="PostgreSqlJobTrackDbContext" />/connection/
///     transaction per call, reloading the actor's current roles and whether the schedule is their own
///     and applying <see cref="ScheduleAccessPolicy" /> itself before writing. Same-user overlap (of
///     schedule-version effective ranges, and of priced additive schedule exceptions) is enforced purely
///     by schema versions 0009/0010's GiST exclusion constraints, so a conflict is caught by translating
///     the resulting <see cref="PostgresException" />, not by taking a lock -- schedule data is not one of
///     ADR 0012's lock domains.
/// </summary>
internal sealed class PostgreSqlScheduleCommandPort : IScheduleCommandPort
{
	private readonly NpgsqlDataSource dataSource;

	/// <summary>Creates the port over the given pooled <see cref="NpgsqlDataSource" />.</summary>
	public PostgreSqlScheduleCommandPort(NpgsqlDataSource dataSource) => this.dataSource = dataSource;

	/// <inheritdoc />
	public async Task<ScheduleVersionResult> AddScheduleVersionAsync(
		AddScheduleVersionRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await EnsureEmployeeExistsAsync(context, request.UserId, cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.UserId, cancellationToken).ConfigureAwait(false);

		var now = SystemClock.Instance.GetCurrentInstant();
		var schedule = new ScheduleVersion(
			ScheduleZoneId.Resolve(request.Schedule.Zone.Id),
			request.Schedule.EffectiveStart,
			request.Schedule.EffectiveEnd,
			request.Schedule.WeeklyIntervals);
		var version = new ScheduleVersionEntity {
			Id = default,
			UserId = request.UserId,
			EffectiveStart = schedule.EffectiveStart,
			EffectiveEnd = schedule.EffectiveEnd,
			IanaTimeZone = schedule.Zone.Id,
			ChangedAt = now,
			RowVersion = 1,
		};
		_ = context.Add(version);

		try {
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

			AuditEventWriter.Add(
				context, request.Context.Actor, now, "add-schedule-version", "user_schedule_version", version.Id.Value,
				request.Context.CorrelationId, null, null,
				new Dictionary<string, string?> {
					["effective_start"] = version.EffectiveStart.ToString(),
					["effective_end"] = version.EffectiveEnd?.ToString(),
					["iana_time_zone"] = version.IanaTimeZone,
				});
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (FindOverlapException(ex) is not null) {
			throw new InvariantViolationException(
				"schedule-version-overlap", "This schedule version's effective range overlaps another for this employee.", ex);
		}

		return new() {
			Id = version.Id,
			UserId = version.UserId,
			Schedule = schedule,
			ChangedAt = version.ChangedAt,
			Version = version.RowVersion,
		};
	}

	/// <inheritdoc />
	public async Task<ScheduleExceptionResult> AddScheduleExceptionAsync(
		AddScheduleExceptionRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await EnsureEmployeeExistsAsync(context, request.UserId, cancellationToken).ConfigureAwait(false);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, request.UserId, cancellationToken).ConfigureAwait(false);
		await EnsureScheduleExceptionDoesNotAlreadyExistAsync(context, request, cancellationToken).ConfigureAwait(false);

		var now = SystemClock.Instance.GetCurrentInstant();
		var exception = new ScheduleExceptionEntity {
			Id = default,
			UserId = request.UserId,
			StartedAt = request.Entry.Interval.Start,
			FinishedAt = request.Entry.Interval.End,
			ScheduleExceptionEffectId = (short)request.Entry.Effect,
			RateOverride = request.Entry.RateOverride,
			Reason = request.Reason,
			CreatedBy = request.Context.Actor,
			ChangedAt = now,
			RowVersion = 1,
		};
		_ = context.Add(exception);

		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			AuditEventWriter.Add(
				context, request.Context.Actor, now, "add-schedule-exception", "user_schedule_exception", exception.Id.Value,
				request.Context.CorrelationId, exception.Reason, null,
				new Dictionary<string, string?> {
					["started_at"] = exception.StartedAt.ToString(),
					["finished_at"] = exception.FinishedAt.ToString(),
					["effect_id"] = exception.ScheduleExceptionEffectId.ToString(CultureInfo.InvariantCulture),
					["rate_override"] = exception.RateOverride?.AmountPerHour.ToString(CultureInfo.InvariantCulture),
				});
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (FindOverlapException(ex) is not null) {
			throw new InvariantViolationException(
				"schedule-exception-priced-additive-overlap",
				"This priced additive exception overlaps another for this employee.", ex);
		}

		return new() {
			Id = exception.Id,
			UserId = exception.UserId,
			Entry = request.Entry,
			Reason = exception.Reason,
			CreatedBy = exception.CreatedBy,
			ChangedAt = exception.ChangedAt,
			Version = exception.RowVersion,
		};
	}

	/// <inheritdoc />
	public async Task<ScheduleVersionResult> CorrectScheduleVersionAsync(
		CorrectScheduleVersionRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		var version = await LoadTrackedVersionAsync(context, request.VersionId, cancellationToken).ConfigureAwait(false);
		EnsureUserMatchesOrThrow(version.UserId, request.UserId, request.VersionId.Value);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, version.UserId, cancellationToken).ConfigureAwait(false);
		CheckVersionOrThrow(version.RowVersion, request.Version);

		var now = SystemClock.Instance.GetCurrentInstant();
		var schedule = new ScheduleVersion(
			ScheduleZoneId.Resolve(request.Schedule.Zone.Id),
			request.Schedule.EffectiveStart,
			request.Schedule.EffectiveEnd,
			request.Schedule.WeeklyIntervals);

		var before = new Dictionary<string, string?> {
			["effective_start"] = version.EffectiveStart.ToString(),
			["effective_end"] = version.EffectiveEnd?.ToString(),
			["iana_time_zone"] = version.IanaTimeZone,
		};

		var existingIntervals = await context.Set<ScheduleIntervalEntity>()
			.Where(i => i.ScheduleVersionId == request.VersionId)
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);
		context.RemoveRange(existingIntervals);

		version.EffectiveStart = schedule.EffectiveStart;
		version.EffectiveEnd = schedule.EffectiveEnd;
		version.IanaTimeZone = schedule.Zone.Id;
		version.ChangedAt = now;
		version.RowVersion += 1;

		foreach (var interval in schedule.WeeklyIntervals) {
			_ = context.Add(new ScheduleIntervalEntity {
				ScheduleVersionId = version.Id,
				DayOfWeek = interval.Day,
				StartTime = interval.Start,
				EndTime = interval.End,
				CrossesMidnight = interval.CrossesMidnight,
			});
		}

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "correct-schedule-version", "user_schedule_version", version.Id.Value,
			request.Context.CorrelationId, request.Reason, before,
			new Dictionary<string, string?> {
				["effective_start"] = version.EffectiveStart.ToString(),
				["effective_end"] = version.EffectiveEnd?.ToString(),
				["iana_time_zone"] = version.IanaTimeZone,
			});

		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException ex) {
			throw new ConcurrencyConflictException(
				$"Expected version {request.Version} for schedule version {request.VersionId} did not match its current version.", ex);
		}
		catch (Exception ex) when (FindOverlapException(ex) is not null) {
			throw new InvariantViolationException(
				"schedule-version-overlap", "This schedule version's effective range overlaps another for this employee.", ex);
		}

		return new() {
			Id = version.Id,
			UserId = version.UserId,
			Schedule = schedule,
			ChangedAt = version.ChangedAt,
			Version = version.RowVersion,
		};
	}

	/// <inheritdoc />
	public async Task<ScheduleExceptionResult> CorrectScheduleExceptionAsync(
		CorrectScheduleExceptionRequest request, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		var exception = await LoadTrackedExceptionAsync(context, request.ExceptionId, cancellationToken).ConfigureAwait(false);
		EnsureUserMatchesOrThrow(exception.UserId, request.UserId, request.ExceptionId.Value);
		await AuthorizeOrThrowAsync(context, request.Context.Actor, exception.UserId, cancellationToken).ConfigureAwait(false);
		CheckVersionOrThrow(exception.RowVersion, request.Version);

		var now = SystemClock.Instance.GetCurrentInstant();
		var before = new Dictionary<string, string?> {
			["started_at"] = exception.StartedAt.ToString(),
			["finished_at"] = exception.FinishedAt.ToString(),
			["effect_id"] = exception.ScheduleExceptionEffectId.ToString(CultureInfo.InvariantCulture),
			["rate_override"] = exception.RateOverride?.AmountPerHour.ToString(CultureInfo.InvariantCulture),
			["reason"] = exception.Reason,
		};

		exception.StartedAt = request.Entry.Interval.Start;
		exception.FinishedAt = request.Entry.Interval.End;
		exception.ScheduleExceptionEffectId = (short)request.Entry.Effect;
		exception.RateOverride = request.Entry.RateOverride;
		exception.Reason = request.Reason;
		exception.ChangedAt = now;
		exception.RowVersion += 1;

		AuditEventWriter.Add(
			context, request.Context.Actor, now, "correct-schedule-exception", "user_schedule_exception", exception.Id.Value,
			request.Context.CorrelationId, request.Reason, before,
			new Dictionary<string, string?> {
				["started_at"] = exception.StartedAt.ToString(),
				["finished_at"] = exception.FinishedAt.ToString(),
				["effect_id"] = exception.ScheduleExceptionEffectId.ToString(CultureInfo.InvariantCulture),
				["rate_override"] = exception.RateOverride?.AmountPerHour.ToString(CultureInfo.InvariantCulture),
				["reason"] = exception.Reason,
			});

		try {
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException ex) {
			throw new ConcurrencyConflictException(
				$"Expected version {request.Version} for schedule exception {request.ExceptionId} did not match its current version.", ex);
		}
		catch (Exception ex) when (FindOverlapException(ex) is not null) {
			throw new InvariantViolationException(
				"schedule-exception-priced-additive-overlap",
				"This priced additive exception overlaps another for this employee.", ex);
		}

		return new() {
			Id = exception.Id,
			UserId = exception.UserId,
			Entry = request.Entry,
			Reason = exception.Reason,
			CreatedBy = exception.CreatedBy,
			ChangedAt = exception.ChangedAt,
			Version = exception.RowVersion,
		};
	}

	/// <summary>
	///     Both schema version 0009's schedule-version and schema version 0010's priced-additive-exception
	///     exclusion constraints have no partial-unique-index backstop the way schema version 0007's
	///     work-session table does, so a violation surfaces here exactly as PostgreSQL reports it
	///     (<c>ExclusionViolation</c>, or <c>DeadlockDetected</c> under genuine concurrent interleaving);
	///     EF's Npgsql execution strategy still re-wraps either in an outer <see cref="InvalidOperationException" />
	///     even on a single, non-retried attempt, so this walks the whole <see cref="Exception.InnerException" />
	///     chain rather than checking one level (matching <c>PostgreSqlWorkSessionCommandPort</c>'s own helper).
	/// </summary>
	private static PostgresException? FindOverlapException(Exception? ex) =>
		ex switch {
			null => null,
			PostgresException pg when IsOverlapSqlState(pg.SqlState) => pg,
			_ => FindOverlapException(ex.InnerException),
		};

	private static bool IsOverlapSqlState(string? sqlState) =>
		sqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ExclusionViolation or PostgresErrorCodes.DeadlockDetected;

	private PostgreSqlJobTrackDbContext CreateContext()
	{
		var options = new DbContextOptionsBuilder<PostgreSqlJobTrackDbContext>()
			.UseNpgsql(dataSource, o => o.UseNodaTime())
			.Options;

		return new(options);
	}

	private static async Task EnsureEmployeeExistsAsync(
		PostgreSqlJobTrackDbContext context, AppUserId userId, CancellationToken cancellationToken)
	{
		if (!await context.Set<AppUserEntity>().AsNoTracking()
				.AnyAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false)) {
			throw new EntityNotFoundException($"Employee {userId} does not exist.");
		}
	}

	private static async Task AuthorizeOrThrowAsync(
		PostgreSqlJobTrackDbContext context, AppUserId actorId, AppUserId targetUserId, CancellationToken cancellationToken)
	{
		var actorRoles = await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);

		if (!ScheduleAccessPolicy.CanManage(actorRoles, actorId == targetUserId)) {
			throw new AuthorizationDeniedException($"Actor {actorId} may not manage schedule data for {targetUserId}.");
		}
	}

	private static async Task EnsureScheduleExceptionDoesNotAlreadyExistAsync(
		PostgreSqlJobTrackDbContext context, AddScheduleExceptionRequest request, CancellationToken cancellationToken)
	{
		var entry = request.Entry;
		var effectId = (short)entry.Effect;
		var rateOverride = entry.RateOverride;
		var duplicateExists = await context.Set<ScheduleExceptionEntity>().AsNoTracking()
			.AnyAsync(e => e.UserId == request.UserId
						   && e.StartedAt == entry.Interval.Start
						   && e.FinishedAt == entry.Interval.End
						   && e.ScheduleExceptionEffectId == effectId
						   && e.RateOverride == rateOverride
						   && e.Reason == request.Reason,
				cancellationToken).ConfigureAwait(false);
		if (duplicateExists) {
			throw new InvariantViolationException(
				"schedule-exception-already-exists",
				"This schedule exception already exists for this employee.");
		}
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

	private static async Task<ScheduleVersionEntity> LoadTrackedVersionAsync(
		PostgreSqlJobTrackDbContext context, ScheduleVersionId versionId, CancellationToken cancellationToken) =>
		await context.Set<ScheduleVersionEntity>().FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken).ConfigureAwait(false)
		?? throw new EntityNotFoundException($"Schedule version {versionId} does not exist.");

	private static async Task<ScheduleExceptionEntity> LoadTrackedExceptionAsync(
		PostgreSqlJobTrackDbContext context, ScheduleExceptionId exceptionId, CancellationToken cancellationToken) =>
		await context.Set<ScheduleExceptionEntity>().FirstOrDefaultAsync(e => e.Id == exceptionId, cancellationToken).ConfigureAwait(false)
		?? throw new EntityNotFoundException($"Schedule exception {exceptionId} does not exist.");

	/// <summary>
	///     A nested route's parent identifier must actually match the row's owner, or the mismatch is
	///     treated identically to a nonexistent row (matching <c>PostgreSqlWorkSessionCommandPort</c>'s
	///     <c>EnsureLeafMatchesOrThrow</c>) -- checked before authorization, alongside the existence check
	///     the load helpers already perform.
	/// </summary>
	private static void EnsureUserMatchesOrThrow(AppUserId actualUserId, AppUserId? expectedUserId, long rowId)
	{
		if (expectedUserId is AppUserId userId && actualUserId != userId) {
			throw new EntityNotFoundException($"Schedule row {rowId} does not belong to employee {userId}.");
		}
	}

	private static void CheckVersionOrThrow(long currentVersion, long expectedVersion)
	{
		if (currentVersion != expectedVersion) {
			throw new ConcurrencyConflictException(
				$"Expected version {expectedVersion} but the current version is {currentVersion}.");
		}
	}
}
