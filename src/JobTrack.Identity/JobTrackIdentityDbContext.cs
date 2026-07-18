namespace JobTrack.Identity;

using Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
///     Common <c>identity_user</c> mapping shared by <see cref="PostgreSqlJobTrackIdentityDbContext" />
///     and <see cref="SqliteJobTrackIdentityDbContext" /> (ADR 0022) — column names and keys only;
///     provider-divergent value conversions live on each subclass.
/// </summary>
public abstract class JobTrackIdentityDbContext : DbContext
{
	private static readonly ValueConverter<AppUserId, long> AppUserIdConverter =
		new(id => id.Value, value => new(value));

	protected JobTrackIdentityDbContext(DbContextOptions options)
		: base(options)
	{
	}

	public DbSet<JobTrackIdentityUser> Users => Set<JobTrackIdentityUser>();

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<JobTrackIdentityUser>(builder => {
			_ = builder.ToTable("identity_user");
			_ = builder.HasKey(e => e.Id);

			_ = builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
			_ = builder.Property(e => e.AppUserId).HasColumnName("app_user_id").HasConversion(AppUserIdConverter).IsRequired();
			_ = builder.Property(e => e.UserName).HasColumnName("user_name").IsRequired();
			_ = builder.Property(e => e.NormalizedUserName).HasColumnName("normalized_user_name").IsRequired();
			_ = builder.Property(e => e.PasswordHash).HasColumnName("password_hash").IsRequired();
			_ = builder.Property(e => e.SecurityStamp).HasColumnName("security_stamp").IsRequired();
			_ = builder.Property(e => e.ConcurrencyStamp).HasColumnName("concurrency_stamp").IsRequired().IsConcurrencyToken();
			_ = builder.Property(e => e.RequiresPasswordChange).HasColumnName("requires_password_change");
			_ = builder.Property(e => e.IsEnabled).HasColumnName("is_enabled");
			_ = builder.Property(e => e.LockoutEnabled).HasColumnName("lockout_enabled");
			_ = builder.Property(e => e.LockoutEnd).HasColumnName("lockout_end");
			_ = builder.Property(e => e.AccessFailedCount).HasColumnName("access_failed_count");
			_ = builder.Property(e => e.TwoFactorEnabled).HasColumnName("two_factor_enabled");
			_ = builder.Property(e => e.AuthenticatorKeyProtected).HasColumnName("authenticator_key_protected");
			_ = builder.Property(e => e.TwoFactorEnabledAt).HasColumnName("two_factor_enabled_at");

			_ = builder.HasIndex(e => e.AppUserId).IsUnique();
			_ = builder.HasIndex(e => e.NormalizedUserName).IsUnique();
		});

		_ = modelBuilder.Entity<JobTrackIdentityRole>(builder => {
			_ = builder.ToTable("identity_role");
			_ = builder.HasKey(e => e.Id);

			_ = builder.Property(e => e.Id).HasColumnName("id");
			_ = builder.Property(e => e.Name).HasColumnName("name").IsRequired();
		});

		_ = modelBuilder.Entity<JobTrackIdentityUserRole>(builder => {
			_ = builder.ToTable("identity_user_role");
			_ = builder.HasKey(e => new { e.IdentityUserId, e.IdentityRoleId });

			_ = builder.Property(e => e.IdentityUserId).HasColumnName("identity_user_id");
			_ = builder.Property(e => e.IdentityRoleId).HasColumnName("identity_role_id");
		});
	}
}
