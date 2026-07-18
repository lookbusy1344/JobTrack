namespace JobTrack.Identity;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
///     SQLite <see cref="JobTrackIdentityDbContext" />: <c>lockout_end</c> as a signed 64-bit UTC tick
///     count (ADR 0007), numerically identical to <c>NodaTime.Instant.ToUnixTimeTicks()</c> because
///     both are 100-nanosecond ticks since the Unix epoch.
/// </summary>
public sealed class SqliteJobTrackIdentityDbContext : JobTrackIdentityDbContext
{
	private static readonly ValueConverter<DateTimeOffset?, long?> NullableUnixTicksConverter =
		new(dateTimeOffset => dateTimeOffset == null ? null : (dateTimeOffset.Value - DateTimeOffset.UnixEpoch).Ticks,
			ticks => ticks == null ? null : DateTimeOffset.UnixEpoch.AddTicks(ticks.Value));

	public SqliteJobTrackIdentityDbContext(DbContextOptions<SqliteJobTrackIdentityDbContext> options)
		: base(options)
	{
	}

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);

		_ = modelBuilder.Entity<JobTrackIdentityUser>().Property(e => e.LockoutEnd).HasConversion(NullableUnixTicksConverter);
		_ = modelBuilder.Entity<JobTrackIdentityUser>().Property(e => e.TwoFactorEnabledAt).HasConversion(NullableUnixTicksConverter);
	}
}
