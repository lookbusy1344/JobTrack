namespace JobTrack.Persistence.PostgreSql;

using Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Shared;
using Shared.Entities;

/// <summary>
///     The PostgreSQL <see cref="DbContext" />: applies <see cref="JobTrackModelConfiguration" /> then
///     the PostgreSQL-specific <see cref="Money" />/<see cref="HourlyRate" /> conversions (impl plan
///     §7.4). <see cref="NodaTime.Instant" /> columns need no manual conversion here — Npgsql's native
///     NodaTime plugin (enabled via <c>UseNodaTime()</c> where the options are built) maps them
///     directly to <c>timestamptz</c>.
/// </summary>
internal sealed class PostgreSqlJobTrackDbContext : DbContext
{
	private static readonly ValueConverter<Money?, decimal?> NullableMoneyConverter =
		new(money => money == null ? null : money.Value.Amount, amount => amount == null ? null : new Money(amount.Value));

	private static readonly ValueConverter<HourlyRate?, decimal?> NullableHourlyRateConverter =
		new(rate => rate == null ? null : rate.Value.AmountPerHour, amount => amount == null ? null : new HourlyRate(amount.Value));

	private static readonly ValueConverter<HourlyRate, decimal> HourlyRateConverter =
		new(rate => rate.AmountPerHour, amount => new(amount));

	public PostgreSqlJobTrackDbContext(DbContextOptions<PostgreSqlJobTrackDbContext> options)
		: base(options)
	{
	}

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		JobTrackModelConfiguration.Configure(modelBuilder);

		_ = modelBuilder.Entity<AppUserEntity>().Property(e => e.DefaultHourlyRate).HasConversion(NullableHourlyRateConverter);
		_ = modelBuilder.Entity<JobNodeEntity>().Property(e => e.ExpectedCost).HasConversion(NullableMoneyConverter);
		_ = modelBuilder.Entity<ScheduleExceptionEntity>().Property(e => e.RateOverride).HasConversion(NullableHourlyRateConverter);
		_ = modelBuilder.Entity<UserCostRateEntity>().Property(e => e.Rate).HasConversion(HourlyRateConverter);
		_ = modelBuilder.Entity<NodeRateOverrideEntity>().Property(e => e.Rate).HasConversion(HourlyRateConverter);

		_ = modelBuilder.Entity<AuditEventEntity>().Property(e => e.BeforeData).HasColumnType("jsonb");
		_ = modelBuilder.Entity<AuditEventEntity>().Property(e => e.AfterData).HasColumnType("jsonb");
	}
}
