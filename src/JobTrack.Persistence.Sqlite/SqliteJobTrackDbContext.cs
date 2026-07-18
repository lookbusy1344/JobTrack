namespace JobTrack.Persistence.Sqlite;

using System.Globalization;
using Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;
using NodaTime.Text;
using Shared;
using Shared.Entities;

/// <summary>
///     The SQLite <see cref="DbContext" />: applies <see cref="JobTrackModelConfiguration" /> then the
///     SQLite-specific <see cref="Instant" /> (UTC-tick, ADR 0007) and <see cref="Money" />/
///     <see
///         cref="HourlyRate" />
///     /plain-<see cref="decimal" /> (fixed-point string, ADR 0009) conversions
///     (impl plan §7.4).
/// </summary>
internal sealed class SqliteJobTrackDbContext : DbContext
{
	private static readonly ValueConverter<Instant, long> InstantConverter =
		new(instant => instant.ToUnixTimeTicks(), ticks => Instant.FromUnixTimeTicks(ticks));

	private static readonly ValueConverter<Instant?, long?> NullableInstantConverter =
		new(instant => instant == null ? null : instant.Value.ToUnixTimeTicks(),
			ticks => ticks == null ? null : Instant.FromUnixTimeTicks(ticks.Value));

	private static readonly ValueConverter<Money?, string?> NullableMoneyConverter =
		new(money => money == null ? null : money.Value.Amount.ToString(CultureInfo.InvariantCulture),
			text => text == null ? null : new Money(decimal.Parse(text, CultureInfo.InvariantCulture)));

	private static readonly ValueConverter<HourlyRate?, string?> NullableHourlyRateConverter =
		new(rate => rate == null ? null : rate.Value.AmountPerHour.ToString(CultureInfo.InvariantCulture),
			text => text == null ? null : new HourlyRate(decimal.Parse(text, CultureInfo.InvariantCulture)));

	private static readonly ValueConverter<HourlyRate, string> HourlyRateConverter =
		new(rate => rate.AmountPerHour.ToString(CultureInfo.InvariantCulture),
			text => new(decimal.Parse(text, CultureInfo.InvariantCulture)));

	private static readonly ValueConverter<decimal?, string?> NullableFixedPointConverter =
		new(value => value == null ? null : value.Value.ToString(CultureInfo.InvariantCulture),
			text => text == null ? null : decimal.Parse(text, CultureInfo.InvariantCulture));

	private static readonly ValueConverter<LocalDate, string> LocalDateConverter =
		new(date => LocalDatePattern.Iso.Format(date), text => LocalDatePattern.Iso.Parse(text).Value);

	private static readonly ValueConverter<LocalDate?, string?> NullableLocalDateConverter =
		new(date => date == null ? null : LocalDatePattern.Iso.Format(date.Value),
			text => text == null ? null : LocalDatePattern.Iso.Parse(text).Value);

	private static readonly ValueConverter<LocalTime, long> LocalTimeConverter =
		new(time => time.TickOfDay, ticks => LocalTime.FromTicksSinceMidnight(ticks));

	private static readonly ValueConverter<Guid, string> GuidConverter =
		new(guid => guid.ToString(), text => Guid.Parse(text));

	public SqliteJobTrackDbContext(DbContextOptions<SqliteJobTrackDbContext> options)
		: base(options)
	{
	}

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		JobTrackModelConfiguration.Configure(modelBuilder);

		_ = modelBuilder.Entity<AppUserEntity>().Property(e => e.DefaultHourlyRate).HasConversion(NullableHourlyRateConverter);

		_ = modelBuilder.Entity<IdentityUserEntity>().Property(e => e.LockoutEnd).HasConversion(NullableInstantConverter);
		_ = modelBuilder.Entity<IdentityUserEntity>().Property(e => e.TwoFactorEnabledAt).HasConversion(NullableInstantConverter);

		_ = modelBuilder.Entity<InitialisedMarkerEntity>().Property(e => e.InitialisedAt).HasConversion(InstantConverter);

		_ = modelBuilder.Entity<JobNodeEntity>().Property(e => e.ExpectedDurationHours).HasConversion(NullableFixedPointConverter);
		_ = modelBuilder.Entity<JobNodeEntity>().Property(e => e.ExpectedCost).HasConversion(NullableMoneyConverter);
		_ = modelBuilder.Entity<JobNodeEntity>().Property(e => e.NeededStart).HasConversion(NullableInstantConverter);
		_ = modelBuilder.Entity<JobNodeEntity>().Property(e => e.NeededFinish).HasConversion(NullableInstantConverter);
		_ = modelBuilder.Entity<JobNodeEntity>().Property(e => e.PostedAt).HasConversion(InstantConverter);
		_ = modelBuilder.Entity<JobNodeEntity>().Property(e => e.ArchivedAt).HasConversion(NullableInstantConverter);

		_ = modelBuilder.Entity<LeafWorkEntity>().Property(e => e.ChangedAt).HasConversion(InstantConverter);

		_ = modelBuilder.Entity<WorkSessionEntity>().Property(e => e.StartedAt).HasConversion(InstantConverter);
		_ = modelBuilder.Entity<WorkSessionEntity>().Property(e => e.FinishedAt).HasConversion(NullableInstantConverter);
		_ = modelBuilder.Entity<WorkSessionEntity>().Property(e => e.ChangedAt).HasConversion(InstantConverter);

		_ = modelBuilder.Entity<ScheduleVersionEntity>().Property(e => e.EffectiveStart).HasConversion(LocalDateConverter);
		_ = modelBuilder.Entity<ScheduleVersionEntity>().Property(e => e.EffectiveEnd).HasConversion(NullableLocalDateConverter);
		_ = modelBuilder.Entity<ScheduleVersionEntity>().Property(e => e.ChangedAt).HasConversion(InstantConverter);

		_ = modelBuilder.Entity<ScheduleIntervalEntity>().Property(e => e.StartTime).HasConversion(LocalTimeConverter);
		_ = modelBuilder.Entity<ScheduleIntervalEntity>().Property(e => e.EndTime).HasConversion(LocalTimeConverter);

		_ = modelBuilder.Entity<ScheduleExceptionEntity>().Property(e => e.StartedAt).HasConversion(InstantConverter);
		_ = modelBuilder.Entity<ScheduleExceptionEntity>().Property(e => e.FinishedAt).HasConversion(InstantConverter);
		_ = modelBuilder.Entity<ScheduleExceptionEntity>().Property(e => e.RateOverride).HasConversion(NullableHourlyRateConverter);
		_ = modelBuilder.Entity<ScheduleExceptionEntity>().Property(e => e.ChangedAt).HasConversion(InstantConverter);

		_ = modelBuilder.Entity<UserCostRateEntity>().Property(e => e.EffectiveStart).HasConversion(InstantConverter);
		_ = modelBuilder.Entity<UserCostRateEntity>().Property(e => e.EffectiveEnd).HasConversion(NullableInstantConverter);
		_ = modelBuilder.Entity<UserCostRateEntity>().Property(e => e.Rate).HasConversion(HourlyRateConverter);
		_ = modelBuilder.Entity<UserCostRateEntity>().Property(e => e.ChangedAt).HasConversion(InstantConverter);

		_ = modelBuilder.Entity<NodeRateOverrideEntity>().Property(e => e.EffectiveStart).HasConversion(InstantConverter);
		_ = modelBuilder.Entity<NodeRateOverrideEntity>().Property(e => e.EffectiveEnd).HasConversion(NullableInstantConverter);
		_ = modelBuilder.Entity<NodeRateOverrideEntity>().Property(e => e.Rate).HasConversion(HourlyRateConverter);
		_ = modelBuilder.Entity<NodeRateOverrideEntity>().Property(e => e.ChangedAt).HasConversion(InstantConverter);

		_ = modelBuilder.Entity<AuditEventEntity>().Property(e => e.OccurredAt).HasConversion(InstantConverter);
		_ = modelBuilder.Entity<AuditEventEntity>().Property(e => e.CorrelationId).HasConversion(GuidConverter);

		_ = modelBuilder.Entity<PersonalAccessTokenEntity>().Property(e => e.CreatedAt).HasConversion(InstantConverter);
		_ = modelBuilder.Entity<PersonalAccessTokenEntity>().Property(e => e.ExpiresAt).HasConversion(InstantConverter);
		_ = modelBuilder.Entity<PersonalAccessTokenEntity>().Property(e => e.RevokedAt).HasConversion(NullableInstantConverter);
		_ = modelBuilder.Entity<PersonalAccessTokenEntity>().Property(e => e.LastUsedAt).HasConversion(NullableInstantConverter);

		_ = modelBuilder.Entity<JobRequestEntity>().Property(e => e.SubmittedAt).HasConversion(InstantConverter);
		_ = modelBuilder.Entity<JobRequestEntity>().Property(e => e.ClosedToRequesterAt).HasConversion(NullableInstantConverter);
		_ = modelBuilder.Entity<JobRequestEntity>().Property(e => e.AcknowledgedAt).HasConversion(NullableInstantConverter);

		_ = modelBuilder.Entity<JobRequestNoteEntity>().Property(e => e.CreatedAt).HasConversion(InstantConverter);
	}
}
