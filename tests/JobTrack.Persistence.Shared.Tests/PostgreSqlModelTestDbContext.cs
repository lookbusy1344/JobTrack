namespace JobTrack.Persistence.Shared.Tests;

using Abstractions;
using Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
///     A throwaway <see cref="DbContext" /> that applies <see cref="JobTrackModelConfiguration" /> plus
///     the minimal PostgreSQL-specific value conversions (impl plan §7.4) needed to build the model
///     and inspect its metadata. It never opens a connection — no test here executes a query against
///     it. The real provider <c>DbContext</c> is a later step.
/// </summary>
internal sealed class PostgreSqlModelTestDbContext : DbContext
{
	private static readonly ValueConverter<Money?, decimal?> NullableMoneyConverter =
		new(money => money == null ? null : money.Value.Amount, amount => amount == null ? null : new Money(amount.Value));

	private static readonly ValueConverter<HourlyRate?, decimal?> NullableHourlyRateConverter =
		new(rate => rate == null ? null : rate.Value.AmountPerHour, amount => amount == null ? null : new HourlyRate(amount.Value));

	private static readonly ValueConverter<HourlyRate, decimal> HourlyRateConverter =
		new(rate => rate.AmountPerHour, amount => new(amount));

	protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
		optionsBuilder.UseNpgsql("Host=localhost;Database=jobtrack_model_test", o => o.UseNodaTime());

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		JobTrackModelConfiguration.Configure(modelBuilder);

		modelBuilder.Entity<AppUserEntity>().Property(e => e.DefaultHourlyRate).HasConversion(NullableHourlyRateConverter);
		modelBuilder.Entity<JobNodeEntity>().Property(e => e.ExpectedCost).HasConversion(NullableMoneyConverter);
		modelBuilder.Entity<ScheduleExceptionEntity>().Property(e => e.RateOverride).HasConversion(NullableHourlyRateConverter);
		modelBuilder.Entity<UserCostRateEntity>().Property(e => e.Rate).HasConversion(HourlyRateConverter);
		modelBuilder.Entity<NodeRateOverrideEntity>().Property(e => e.Rate).HasConversion(HourlyRateConverter);

		modelBuilder.Entity<AuditEventEntity>().Property(e => e.BeforeData).HasColumnType("jsonb");
		modelBuilder.Entity<AuditEventEntity>().Property(e => e.AfterData).HasColumnType("jsonb");
	}
}
