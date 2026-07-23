namespace JobTrack.Identity;

using Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;

/// <summary>
///     Provider registration for <c>JobTrack.Identity</c> (ADR 0022, plan §8.2) — composed by
///     <c>JobTrack.Web</c> and <c>JobTrack.AdminCli</c>. Registers the provider-specific
///     <see cref="JobTrackIdentityDbContext" /> and <see cref="IdentityCore{TUser}" /> wired to
///     <see cref="JobTrackUserStore" />, returning the <see cref="IdentityBuilder" /> so the host can
///     chain <c>AddSignInManager()</c>/cookie authentication — both require the ASP.NET Core shared
///     framework that this project deliberately does not reference (ADR 0022).
/// </summary>
public static class ServiceCollectionExtensions
{
	// Matches the busy-timeout every JobTrack.Persistence.Sqlite command port sets via
	// PRAGMA busy_timeout before writing (plan §6.2: "on every connection, configure and verify
	// foreign keys, busy timeout..."). Identity opens its own SqliteConnection independently of
	// those ports, so without this it silently fell back to Microsoft.Data.Sqlite's 30-second
	// default -- long enough that a login landing mid-write elsewhere blocked for up to 30 seconds
	// (surfaced by JobTrack.Web.EndToEndTests's browser fixture, which starts a Sign-in navigation
	// while a job-node seed write is still settling) instead of failing fast under real contention.
	private const int SqliteBusyTimeoutSeconds = 5;

	/// <summary>
	///     Data Protection application name (ADR 0037): must be identical wherever
	///     <see cref="JobTrackUserStore" /> runs -- <c>JobTrack.Web</c> and <c>JobTrack.AdminCli</c> --
	///     so a TOTP shared secret encrypted by one host can still be decrypted by the other.
	/// </summary>
	private const string DataProtectionApplicationName = "JobTrack";

	public static IdentityBuilder AddJobTrackIdentityPostgreSql(this IServiceCollection services, string connectionString)
	{
		_ = services.AddDbContext<JobTrackIdentityDbContext, PostgreSqlJobTrackIdentityDbContext>(options => options.UseNpgsql(connectionString));

		return services.AddJobTrackIdentityCore();
	}

	public static IdentityBuilder AddJobTrackIdentitySqlite(this IServiceCollection services, string connectionString)
	{
		var sqliteConnectionString = new SqliteConnectionStringBuilder(connectionString) {
			ForeignKeys = true,
			DefaultTimeout = SqliteBusyTimeoutSeconds,
		}.ConnectionString;

		_ = services.AddDbContext<JobTrackIdentityDbContext, SqliteJobTrackIdentityDbContext>(options =>
			options.UseSqlite(sqliteConnectionString).AddInterceptors(new SqliteWalPragmaInterceptor()));

		return services.AddJobTrackIdentityCore();
	}

	private static IdentityBuilder AddJobTrackIdentityCore(this IServiceCollection services)
	{
		// ADR 0016: JobTrackUserStore's sole source of "now". TryAddSingleton, not AddSingleton --
		// a host (JobTrack.Web/JobTrack.AdminCli) that already registered its own IClock keeps that
		// registration; this only supplies the default for callers (tests, ad hoc tooling) that
		// haven't registered one themselves.
		services.TryAddSingleton<IClock>(SystemClock.Instance);

		// Also resolvable as its concrete type (not only as IUserStore<JobTrackIdentityUser>) so the
		// bearer authentication scheme (ADR 0029) can reach FindByAppUserIdAsync, which the
		// IUserStore<T>/UserManager<T> surface has no equivalent lookup for.
		_ = services.AddScoped<JobTrackUserStore>();

		// ADR 0037: encrypts the TOTP shared secret at rest. AddDataProtection() is idempotent, so
		// this is safe to call even when the host (JobTrack.Web) has already registered it itself.
		_ = services.AddDataProtection().SetApplicationName(DataProtectionApplicationName);

		return services.AddIdentityCore<JobTrackIdentityUser>(options => {
			// Relaxed on purpose (PasswordPolicy): a letter and a digit, minimum length only --
			// no required mixed case or symbol. RequireDigit stays on IdentityOptions since it
			// already expresses "any digit" case-insensitively; the letter half needs
			// RequiresLetterPasswordValidator because RequireLowercase/RequireUppercase are each
			// case-specific and there is no "either case" flag.
			options.Password.RequiredLength = PasswordPolicy.MinimumLength;
			options.Password.RequireDigit = true;
			options.Password.RequireLowercase = false;
			options.Password.RequireUppercase = false;
			options.Password.RequireNonAlphanumeric = false;
			options.Password.RequiredUniqueChars = 1;
		})
			.AddUserStore<JobTrackUserStore>()
			.AddClaimsPrincipalFactory<JobTrackUserClaimsPrincipalFactory>()
			.AddPasswordValidator<RequiresLetterPasswordValidator>()
			// ADR 0037: registers AuthenticatorTokenProvider<TUser> (RFC 6238 TOTP verification) under
			// TokenOptions.DefaultAuthenticatorProvider, the name SignInManager.TwoFactorAuthenticator-
			// SignInAsync looks up by default -- no hand-rolled TOTP code, the framework's own
			// verification is used as-is. AddDefaultTokenProviders() is unavailable here: it lives in
			// the full Microsoft.AspNetCore.Identity package, which ADR 0022 deliberately avoids.
			.AddTokenProvider<AuthenticatorTokenProvider<JobTrackIdentityUser>>(TokenOptions.DefaultAuthenticatorProvider);
	}
}
