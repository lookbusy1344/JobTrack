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
		: base(options) { }

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
			_ = builder.Property(e => e.PasskeyUserHandle).HasColumnName("passkey_user_handle").HasMaxLength(PasskeyPolicy.UserHandleEncodedLength);

			_ = builder.HasIndex(e => e.AppUserId).IsUnique();
			_ = builder.HasIndex(e => e.NormalizedUserName).IsUnique();
			_ = builder.HasIndex(e => e.PasskeyUserHandle).IsUnique();
		});

		_ = modelBuilder.Entity<JobTrackIdentityUserPasskey>(builder => {
			_ = builder.ToTable("identity_user_passkey");
			_ = builder.HasKey(e => e.CredentialId);

			_ = builder.Property(e => e.CredentialId).HasColumnName("credential_id").HasMaxLength(PasskeyPolicy.MaximumCredentialIdByteLength);
			_ = builder.Property(e => e.IdentityUserId).HasColumnName("identity_user_id");
			_ = builder.Property(e => e.Name).HasColumnName("name").IsRequired();
			_ = builder.Property(e => e.NormalizedName).HasColumnName("normalized_name").IsRequired();
			_ = builder.Property(e => e.PublicKey).HasColumnName("public_key").HasMaxLength(PasskeyPolicy.MaximumPublicKeyByteLength).IsRequired();
			_ = builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
			_ = builder.Property(e => e.SignCount).HasColumnName("sign_count");
			_ = builder.Property(e => e.Transports).HasColumnName("transports").HasMaxLength(PasskeyPolicy.MaximumTransportsLength);
			_ = builder.Property(e => e.IsUserVerified).HasColumnName("is_user_verified");
			_ = builder.Property(e => e.IsBackupEligible).HasColumnName("is_backup_eligible");
			_ = builder.Property(e => e.IsBackedUp).HasColumnName("is_backed_up");
			_ = builder.Property(e => e.AttestationObject).HasColumnName("attestation_object").HasMaxLength(PasskeyPolicy.MaximumAttestationObjectByteLength).IsRequired();
			_ = builder.Property(e => e.ClientDataJson).HasColumnName("client_data_json").HasMaxLength(PasskeyPolicy.MaximumClientDataJsonByteLength).IsRequired();
			_ = builder.Property(e => e.RowVersion).HasColumnName("row_version").IsConcurrencyToken();

			_ = builder.HasIndex(e => new
			{
				e.IdentityUserId,
				e.NormalizedName,
			}, "identity_user_passkey_user_normalized_name_idx").IsUnique();
			_ = builder.HasIndex(e => e.IdentityUserId, "identity_user_passkey_identity_user_id_idx");

			_ = builder.HasOne<JobTrackIdentityUser>().WithMany().HasForeignKey(e => e.IdentityUserId).OnDelete(DeleteBehavior.Restrict);
		});

		_ = modelBuilder.Entity<JobTrackIdentityRole>(builder => {
			_ = builder.ToTable("identity_role");
			_ = builder.HasKey(e => e.Id);

			_ = builder.Property(e => e.Id).HasColumnName("id");
			_ = builder.Property(e => e.Name).HasColumnName("name").IsRequired();
		});

		_ = modelBuilder.Entity<JobTrackIdentityUserRole>(builder => {
			_ = builder.ToTable("identity_user_role");
			_ = builder.HasKey(e => new
			{
				e.IdentityUserId,
				e.IdentityRoleId,
			});

			_ = builder.Property(e => e.IdentityUserId).HasColumnName("identity_user_id");
			_ = builder.Property(e => e.IdentityRoleId).HasColumnName("identity_role_id");
		});
	}
}
