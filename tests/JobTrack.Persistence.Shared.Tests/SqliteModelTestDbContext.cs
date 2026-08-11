namespace JobTrack.Persistence.Shared.Tests;

using System.Globalization;
using Abstractions;
using Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;
using NodaTime.Text;

/// <summary>
///     A throwaway <see cref="DbContext" /> that applies <see cref="JobTrackModelConfiguration" /> plus
///     the minimal SQLite-specific value conversions (ADR 0007, ADR 0009) needed to build the model
///     and inspect its metadata. It never opens a connection — no test here executes a query against
///     it. The real provider <c>DbContext</c> is a later step.
/// </summary>
internal sealed class SqliteModelTestDbContext : DbContext
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

	protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
		optionsBuilder.UseSqlite("Data Source=:memory:");

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		JobTrackModelConfiguration.Configure(modelBuilder);

		modelBuilder.Entity<AppUserEntity>().Property(e => e.DefaultHourlyRate).HasConversion(NullableHourlyRateConverter);

		modelBuilder.Entity<IdentityUserEntity>().Property(e => e.LockoutEnd).HasConversion(NullableInstantConverter);
		modelBuilder.Entity<IdentityUserEntity>().Property(e => e.TwoFactorEnabledAt).HasConversion(NullableInstantConverter);

		modelBuilder.Entity<InitialisedMarkerEntity>().Property(e => e.InitialisedAt).HasConversion(InstantConverter);

		modelBuilder.Entity<JobNodeEntity>().Property(e => e.ExpectedDurationHours).HasConversion(NullableFixedPointConverter);
		modelBuilder.Entity<JobNodeEntity>().Property(e => e.ExpectedCost).HasConversion(NullableMoneyConverter);
		modelBuilder.Entity<JobNodeEntity>().Property(e => e.NeededStart).HasConversion(NullableInstantConverter);
		modelBuilder.Entity<JobNodeEntity>().Property(e => e.NeededFinish).HasConversion(NullableInstantConverter);
		modelBuilder.Entity<JobNodeEntity>().Property(e => e.PostedAt).HasConversion(InstantConverter);
		modelBuilder.Entity<JobNodeEntity>().Property(e => e.ArchivedAt).HasConversion(NullableInstantConverter);

		modelBuilder.Entity<LeafWorkEntity>().Property(e => e.ChangedAt).HasConversion(InstantConverter);

		modelBuilder.Entity<WorkSessionEntity>().Property(e => e.StartedAt).HasConversion(InstantConverter);
		modelBuilder.Entity<WorkSessionEntity>().Property(e => e.FinishedAt).HasConversion(NullableInstantConverter);
		modelBuilder.Entity<WorkSessionEntity>().Property(e => e.ChangedAt).HasConversion(InstantConverter);

		modelBuilder.Entity<ScheduleVersionEntity>().Property(e => e.EffectiveStart).HasConversion(LocalDateConverter);
		modelBuilder.Entity<ScheduleVersionEntity>().Property(e => e.EffectiveEnd).HasConversion(NullableLocalDateConverter);
		modelBuilder.Entity<ScheduleVersionEntity>().Property(e => e.ChangedAt).HasConversion(InstantConverter);

		modelBuilder.Entity<ScheduleIntervalEntity>().Property(e => e.StartTime).HasConversion(LocalTimeConverter);
		modelBuilder.Entity<ScheduleIntervalEntity>().Property(e => e.EndTime).HasConversion(LocalTimeConverter);

		modelBuilder.Entity<ScheduleExceptionEntity>().Property(e => e.StartedAt).HasConversion(InstantConverter);
		modelBuilder.Entity<ScheduleExceptionEntity>().Property(e => e.FinishedAt).HasConversion(InstantConverter);
		modelBuilder.Entity<ScheduleExceptionEntity>().Property(e => e.RateOverride).HasConversion(NullableHourlyRateConverter);
		modelBuilder.Entity<ScheduleExceptionEntity>().Property(e => e.ChangedAt).HasConversion(InstantConverter);

		modelBuilder.Entity<UserCostRateEntity>().Property(e => e.EffectiveStart).HasConversion(InstantConverter);
		modelBuilder.Entity<UserCostRateEntity>().Property(e => e.EffectiveEnd).HasConversion(NullableInstantConverter);
		modelBuilder.Entity<UserCostRateEntity>().Property(e => e.Rate).HasConversion(HourlyRateConverter);
		modelBuilder.Entity<UserCostRateEntity>().Property(e => e.ChangedAt).HasConversion(InstantConverter);

		modelBuilder.Entity<NodeRateOverrideEntity>().Property(e => e.EffectiveStart).HasConversion(InstantConverter);
		modelBuilder.Entity<NodeRateOverrideEntity>().Property(e => e.EffectiveEnd).HasConversion(NullableInstantConverter);
		modelBuilder.Entity<NodeRateOverrideEntity>().Property(e => e.Rate).HasConversion(HourlyRateConverter);
		modelBuilder.Entity<NodeRateOverrideEntity>().Property(e => e.ChangedAt).HasConversion(InstantConverter);

		modelBuilder.Entity<AuditEventEntity>().Property(e => e.OccurredAt).HasConversion(InstantConverter);
		modelBuilder.Entity<AuditEventEntity>().Property(e => e.CorrelationId).HasConversion(GuidConverter);

		modelBuilder.Entity<PersonalAccessTokenEntity>().Property(e => e.CreatedAt).HasConversion(InstantConverter);
		modelBuilder.Entity<PersonalAccessTokenEntity>().Property(e => e.ExpiresAt).HasConversion(InstantConverter);
		modelBuilder.Entity<PersonalAccessTokenEntity>().Property(e => e.RevokedAt).HasConversion(NullableInstantConverter);
		modelBuilder.Entity<PersonalAccessTokenEntity>().Property(e => e.LastUsedAt).HasConversion(NullableInstantConverter);

		modelBuilder.Entity<JobRequestEntity>().Property(e => e.SubmittedAt).HasConversion(InstantConverter);
		modelBuilder.Entity<JobRequestEntity>().Property(e => e.ClosedToRequesterAt).HasConversion(NullableInstantConverter);
		modelBuilder.Entity<JobRequestEntity>().Property(e => e.AcknowledgedAt).HasConversion(NullableInstantConverter);

		modelBuilder.Entity<JobRequestNoteEntity>().Property(e => e.CreatedAt).HasConversion(InstantConverter);
	}
}
