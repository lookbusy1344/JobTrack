namespace JobTrack.Identity;

using System.Reflection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

/// <summary>
///     PostgreSQL <see cref="JobTrackIdentityDbContext" />: <c>lockout_end</c> maps natively to
///     <c>timestamptz</c> via Npgsql's built-in <see cref="DateTimeOffset" /> support — no manual
///     conversion (contrast <see cref="SqliteJobTrackIdentityDbContext" />). Also the multi-instance
///     data-protection key repository (ADR 0066 Stage 2,
///     docs/plans/2026-07-26-multi-instance-web-deployment-plan.md §2.2) — PostgreSQL-only, since
///     SQLite receives no multi-instance schema or implementation work under that plan's provider
///     boundary.
/// </summary>
public sealed class PostgreSqlJobTrackIdentityDbContext : JobTrackIdentityDbContext, IDataProtectionKeyContext
{
	public PostgreSqlJobTrackIdentityDbContext(DbContextOptions<PostgreSqlJobTrackIdentityDbContext> options)
		: base(options)
	{
	}

	public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

	internal IQueryable<RateLimitConsumeResult> RateLimitTryConsume(
		string purpose,
		byte[] partitionDigest,
		byte[]? backstopDigest,
		DateTimeOffset now,
		int windowSeconds,
		int permitLimit,
		int backstopPermitLimit,
		int maxPartitionCount) =>
		FromExpression(() => RateLimitTryConsume(
			purpose,
			partitionDigest,
			backstopDigest,
			now,
			windowSeconds,
			permitLimit,
			backstopPermitLimit,
			maxPartitionCount));

	internal float ReadRateLimitLivePartitionCount() =>
		Database.SqlQuery<float>($"SELECT rate_limit_live_partition_count() AS \"Value\"").Single();

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);

		_ = modelBuilder.Entity<DataProtectionKey>(builder => {
			_ = builder.ToTable("data_protection_key");
			_ = builder.HasKey(e => e.Id);

			_ = builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
			_ = builder.Property(e => e.FriendlyName).HasColumnName("friendly_name");
			_ = builder.Property(e => e.Xml).HasColumnName("xml");
		});

		_ = modelBuilder.Entity<RateLimitConsumeResult>(builder => {
			_ = builder.HasNoKey();
			_ = builder.Property(result => result.OutAllowed).HasColumnName("out_allowed");
			_ = builder.Property(result => result.OutRowsPruned).HasColumnName("out_rows_pruned");
		});

		var rateLimitTryConsume = typeof(PostgreSqlJobTrackIdentityDbContext).GetMethod(
									  nameof(RateLimitTryConsume), BindingFlags.Instance | BindingFlags.NonPublic)
								  ?? throw new InvalidOperationException($"Could not resolve {nameof(RateLimitTryConsume)} for EF mapping.");
		_ = modelBuilder.HasDbFunction(rateLimitTryConsume).HasName("rate_limit_try_consume");
	}
}
