namespace JobTrack.Web;

using System.Collections.Frozen;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Application;
using Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Persistence.PostgreSql;
using Persistence.Sqlite;
using IPNetwork = System.Net.IPNetwork;
using SystemClock = NodaTime.SystemClock;

// Not `static class`: WebApplicationFactory<Program> (JobTrack.Web.IntegrationTests) requires a
// non-static entry-point type argument.
public sealed class Program
{
	private const string SqliteProviderName = "Sqlite";
	private const string PostgreSqlProviderName = "PostgreSql";
	private const string DomainDataSourceKey = "JobTrackDomain";
	private const string HistoryDeletionDataSourceKey = "JobTrackHistoryDeletion";
	private const string CredentialAdministrationDataSourceKey = "JobTrackCredentialAdministration";
	private const string PatManagementDataSourceKey = "JobTrackPatManagement";
	private const string PatAuthenticationDataSourceKey = "JobTrackPatAuthentication";
	private const string CookieOrBearerSchemeName = "JobTrackCookieOrBearer";
	private const int LoginRateLimitPermitLimit = 20;
	private const int LoginRateLimitWindowSeconds = 60;

	// Ops-tunable, default unchanged (20/60s): a shared BrowserFixture-hosted process drives many
	// sequential /Account/Login GET+POST pairs across one test class's real-browser suite within a
	// single fixed window, which the unconfigured default budget cannot absorb -- browser tests
	// override these via environment variables for their own child process only (see
	// JobTrack.Web.EndToEndTests.BrowserFixture), production keeps the unconfigured default.
	private const string LoginRateLimitPermitLimitConfigKey = "RateLimiting:LoginPermitLimit";
	private const string LoginRateLimitWindowSecondsConfigKey = "RateLimiting:LoginWindowSeconds";

	// External API plan §4.4: per-client/per-user throttling distinct from the login limiter above
	// -- partitioned by the caller's own identity (bearer PAT or cookie session both resolve to the
	// same authenticated user name) rather than a single shared window, since a legitimate CLI
	// consumer's steady traffic must not be capped by other callers' usage. The policy name is
	// declared on JobTrackApi, not here, since that is where it's attached to the route group.
	private const int ApiRateLimitPermitLimit = 120;
	private const int ApiRateLimitWindowSeconds = 60;
	private const int RateLimitMaxPartitionCount = 4096;
	private const string ApiRateLimitPermitLimitConfigKey = "RateLimiting:ApiPermitLimit";
	private const string ApiRateLimitWindowSecondsConfigKey = "RateLimiting:ApiWindowSeconds";
	private const string RateLimitMaxPartitionCountConfigKey = "RateLimiting:MaxPartitionCount";

	// Matches LoginAttemptRateLimiter's own DefaultBackstopPermitMultiplier so the two topologies
	// enforce the identical backstop ceiling for the same configured permit limit.
	private const int LoginRateLimitBackstopPermitMultiplier = 20;

	// ADR 0066 Stage 5: the shared PostgreSQL rate-limit primitive, alongside the existing in-process
	// limiters. "PostgreSql" is valid only when Database:Provider is itself PostgreSql, matching
	// DataProtection:Store's own constraint immediately below.
	private const string RateLimitingStoreConfigKey = "RateLimiting:Store";
	private const string RateLimitingStoreInProcess = "InProcess";
	private const string RateLimitingStorePostgreSql = "PostgreSql";
	private const string RateLimitedProblemType = "/problems/rate-limited";
	private const string RateLimitStoreUnavailableProblemType = "/problems/rate-limit-store-unavailable";

	private const int MaxFailedAccessAttempts = 5;

	private const int LockoutMinutes = 15;

	// ADR 0057 (§2.3): doubles as the absolute session ceiling, not only the sliding-renewal window --
	// SlidingExpiration renews the cookie for another window this long every time it passes the
	// halfway mark, but OnValidatePrincipal below rejects the session outright once it has run this
	// long from its original sign-in, regardless of how recently it renewed.
	private const int AuthenticationCookieExpirationHours = 8;

	// Threat-model row 5 (XSS, TC-WEB-AUTHN-007; plan §8.2: "restrictive Content Security Policy,
	// frame restrictions, MIME sniffing protection, referrer policy"). The site has no inline
	// scripts/styles and no third-party origins (_Layout.cshtml: only same-origin site.css/site.js),
	// so this stays maximally restrictive rather than adding 'unsafe-inline'/'unsafe-eval'.
	// img-src allows 'data:' for ManageTwoFactor.cshtml's server-rendered base64 TOTP QR code
	// (QRCodeGenerator PNG output embedded directly as an <img src="data:image/png;base64,...">).
	private const string ContentSecurityPolicy =
		"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; " +
		"base-uri 'self'; frame-ancestors 'none'; form-action 'self'";

	private const string ContentSecurityPolicyHeaderName = "Content-Security-Policy";
	private const string ContentTypeOptionsHeaderName = "X-Content-Type-Options";
	private const string ContentTypeOptionsHeaderValue = "nosniff";
	private const string FrameOptionsHeaderName = "X-Frame-Options";
	private const string FrameOptionsHeaderValue = "DENY";
	private const string ReferrerPolicyHeaderName = "Referrer-Policy";
	private const string ReferrerPolicyHeaderValue = "no-referrer";
	private const string CacheControlHeaderName = "Cache-Control";
	private const string CacheControlHeaderValue = "no-store, no-cache";
	private const string PragmaHeaderName = "Pragma";
	private const string PragmaHeaderValue = "no-cache";

	// Plan §8.2 / fix-plan §2.4: trust no reverse proxy by default. Outside Development, at least
	// one of these must be configured or startup fails closed rather than silently trusting
	// whatever forwarded IP/scheme a request happens to present.
	private const string ForwardedHeadersKnownProxiesConfigKey = "ForwardedHeaders:KnownProxies";
	private const string ForwardedHeadersKnownNetworksConfigKey = "ForwardedHeaders:KnownNetworks";

	// Host filtering. ASP.NET Core's default when AllowedHosts is unset or "*" is to accept any Host
	// header at all, which lets a request forge the absolute URLs the app generates from it (password
	// reset/redirect links, cache keys) and defeats virtual-host isolation on a shared front end.
	// Outside Development this must name the deployment's own hosts, so both the wildcard and an
	// absent value fail startup closed -- the same posture as the two checks below. Subdomain
	// wildcards ("*.run.app") stay usable; only the bare catch-all is rejected.
	private const string AllowedHostsConfigKey = "AllowedHosts";
	private const string AllowedHostsCatchAll = "*";
	private const char AllowedHostsSeparator = ';';

	// Plan §8.2: data-protection keys persisted outside the application directory. Outside
	// Development, an unconfigured path fails startup closed rather than falling back to the
	// framework's ephemeral/registry-based default key ring.
	private const string DataProtectionKeyPathConfigKey = "DataProtection:KeyPath";
	private const string DataProtectionCertificatePathConfigKey = "DataProtection:CertificatePath";
	private const string DataProtectionCertificatePasswordPathConfigKey = "DataProtection:CertificatePasswordPath";

	// ADR 0066 Stage 2: the multi-instance PostgreSQL key repository, alongside the existing
	// filesystem/GCS store. "PostgreSql" is valid only when Database:Provider is itself PostgreSql --
	// there is no SQLite equivalent (plan's provider boundary) and no other database to persist keys
	// in when the domain connection is SQLite.
	private const string DataProtectionStoreConfigKey = "DataProtection:Store";
	private const string DataProtectionStoreFileSystem = "FileSystem";
	private const string DataProtectionStorePostgreSql = "PostgreSql";

	// ADR 0066 Stage 6: the topology-level guard tying DataProtection:Store, RateLimiting:Store, and
	// Database:Provider together. Each per-store selector above is independently satisfiable with a
	// process-local choice, which is exactly the configuration that silently loses cross-host
	// correctness under more than one Cloud Run instance -- MultiInstance fails startup closed unless
	// every one of them already names its shared PostgreSQL-backed implementation.
	private const string DeploymentTopologyConfigKey = "Deployment:Topology";
	private const string DeploymentTopologySingleInstance = "SingleInstance";
	private const string DeploymentTopologyMultiInstance = "MultiInstance";
	private const string RequireSecureCookiesConfigKey = "Security:RequireSecureCookies";

	// No attachments/file uploads exist in this content model (fix-plan non-goals), so request
	// bodies are plain JSON/form payloads -- generous headroom over the largest legitimate body
	// (a schedule version with a full week of intervals) without leaving the limit effectively
	// unbounded.
	private const long MaxRequestBodyBytes = 64 * 1024;
	private const string RequestTooLargeProblemType = "/problems/request-too-large";

	private const int RequestTimeoutSeconds = 30;
	private const int ReadinessProbeCacheSeconds = 2;

	// Defense against a slow/stalled request body (e.g. slowloris-style resource exhaustion,
	// security review remediation §2.6): AddRequestTimeouts' RequestTimeoutSeconds above only
	// cancels HttpContext.RequestAborted, which Razor Pages' built-in form model binding does not
	// consistently observe while awaiting more body bytes from Kestrel -- a request trickling in
	// below this rate is cut off at the Kestrel connection level instead, independent of whether
	// higher-level model-binding code cooperates with cancellation.
	private const int MinRequestBodyDataRateBytesPerSecond = 240;
	private const int MinRequestBodyDataRateGracePeriodSeconds = 5;

	// Same-origin cookie application with no browser-facing cross-origin API consumer (fix-plan
	// non-goals: no SPA/bearer-token flow without one) -- this policy exists to make that a
	// deliberate, named choice (plan §8.2 "carefully scoped cross-origin policy") rather than an
	// absence of configuration that happens to have the same effect.
	private const string CorsPolicyName = "NoCrossOrigin";

	/// <summary>
	///     The only pages exempt from <c>AuthorizeFolder("/")</c> above: the sign-in sequence itself,
	///     the sign-out endpoint, the access-denied page, the error page, and the landing page (which
	///     holds no content of its own and redirects an anonymous visitor to sign-in). Kept in step with
	///     <c>WebHostSecurityArchitectureTests.AnonymousPageAllowlist</c>, which asserts the same set
	///     declares no <c>[Authorize]</c> attribute.
	/// </summary>
	// FrozenSet, not `static readonly string[]` (FDG §9.12): a span-typed property cannot hold a
	// reference-type collection expression, and `readonly` on an array would freeze the reference
	// while leaving the elements writable.
	private static readonly FrozenSet<string> AnonymousPages = FrozenSet.ToFrozenSet(
		[
			"/Index",
			"/Error",
			"/Account/Login",
			"/Account/LoginTwoFactor",
			"/Account/Logout",
			"/Account/AccessDenied",
		],
		StringComparer.Ordinal);

	private Program() { }

	public static void Main(string[] args)
	{
		var builder = WebApplication.CreateBuilder(args);

		// Add services to the container.
		_ = builder.Services.AddRazorPages(options => {
			_ = options.Conventions.AddFolderApplicationModelConvention("/", model => {
				model.Filters.Add(new RequiresPasswordChangePageFilter());
				model.Filters.Add(new RequiresRecentAuthenticationPageFilter());
			});

			// Closed by default. Every page already declares its own `[Authorize(Policy = ...)]`
			// (WebHostSecurityArchitectureTests enforces that), but an attribute is opt-in: a page
			// added without one would ship publicly readable and only a test would object. This
			// folder convention makes the framework itself the backstop -- an authenticated user is
			// required for every page under `/` unless it is named below -- so a forgotten attribute
			// degrades to "any signed-in employee" rather than "the whole internet". The per-page
			// role policies still apply on top: RequireAuthorization ANDs, it does not replace.
			_ = options.Conventions.AuthorizeFolder("/");
			foreach (var page in AnonymousPages) {
				_ = options.Conventions.AllowAnonymousToPage(page);
			}
		});

		_ = builder.Services.AddScoped<IViewerTimeZoneResolver, ViewerTimeZoneResolver>();
		_ = builder.Services.AddSingleton<IClock>(SystemClock.Instance);

		// A newly issued personal access token's plaintext travels from the issuing POST to the
		// redirected GET via PendingPatDeliveryCookie -- a short-lived, actor-bound, data-protected
		// cookie (ADR 0066 Stage 4) -- rather than the old in-process PendingPatDeliveryStore, whose
		// Dictionary meant the redirect had to land back on the same host.

		// Remembered per-page filter selections (FilterMemory) are backed by
		// CookieFilterMemoryStore -- a small, time-limited, data-protected, principal-bound cookie
		// -- rather than ASP.NET Core session (ADR 0066 Stage 3): session's AddDistributedMemoryCache
		// is process-local, so a filter set on one host silently appeared to reset on another.

		var databaseProvider = builder.Configuration["Database:Provider"]
							   ?? throw new InvalidOperationException("Database:Provider is not configured.");
		var identityConnectionString = builder.Configuration.GetConnectionString("JobTrackIdentity")
									   ?? throw new InvalidOperationException("ConnectionStrings:JobTrackIdentity is not configured.");

		// Security review remediation §2.9: outside Development, a remote PostgreSQL host must be
		// reached over an authenticated encrypted channel -- Npgsql's own default (SSL Mode=Prefer)
		// neither guarantees encryption nor authenticates the server. Loopback/Unix-socket
		// connections (the only shape a same-host deployment or this repository's local dev setup
		// uses) are exempt inside the validator itself.
		if (databaseProvider == PostgreSqlProviderName && !builder.Environment.IsDevelopment()) {
			PostgreSqlTransportSecurity.Validate(identityConnectionString);
		}

		var identityBuilder = databaseProvider switch {
			PostgreSqlProviderName => builder.Services.AddJobTrackIdentityPostgreSql(identityConnectionString),
			SqliteProviderName => builder.Services.AddJobTrackIdentitySqlite(identityConnectionString),
			_ => throw new InvalidOperationException($"Unknown Database:Provider '{databaseProvider}'."),
		};
		_ = identityBuilder.AddSignInManager<JobTrackSignInManager>();

		switch (databaseProvider) {
			case PostgreSqlProviderName:
				// Security review remediation §2.6: IJobTrackClient authenticates as the
				// jobtrack_domain PostgreSQL role, a distinct credential from ConnectionStrings:
				// JobTrackIdentity's jobtrack_identity role that ASP.NET Core Identity's own
				// sign-in path (above) uses -- a compromised credential on one connection no
				// longer automatically carries the other's blast radius.
				var domainConnectionString = builder.Configuration.GetConnectionString("JobTrackDomain")
											 ?? throw new InvalidOperationException("ConnectionStrings:JobTrackDomain is not configured.");
				var historyDeletionConnectionString = builder.Configuration.GetConnectionString("JobTrackHistoryDeletion")
													  ?? throw new InvalidOperationException(
														  "ConnectionStrings:JobTrackHistoryDeletion is not configured.");
				var credentialAdministrationConnectionString = builder.Configuration.GetConnectionString("JobTrackCredentialAdministration")
															   ?? throw new InvalidOperationException(
																   "ConnectionStrings:JobTrackCredentialAdministration is not configured.");
				var patManagementConnectionString = builder.Configuration.GetConnectionString("JobTrackPatManagement")
													?? throw new InvalidOperationException(
														"ConnectionStrings:JobTrackPatManagement is not configured.");
				var patAuthenticationConnectionString = builder.Configuration.GetConnectionString("JobTrackPatAuthentication")
														?? throw new InvalidOperationException(
															"ConnectionStrings:JobTrackPatAuthentication is not configured.");
				if (!builder.Environment.IsDevelopment()) {
					PostgreSqlTransportSecurity.Validate(domainConnectionString);
					PostgreSqlTransportSecurity.Validate(historyDeletionConnectionString);
					PostgreSqlTransportSecurity.Validate(credentialAdministrationConnectionString);
					PostgreSqlTransportSecurity.Validate(patManagementConnectionString);
					PostgreSqlTransportSecurity.Validate(patAuthenticationConnectionString);
				}

				_ = builder.Services.AddKeyedSingleton<NpgsqlDataSource>(
					DomainDataSourceKey, (_, _) => new NpgsqlDataSourceBuilder(domainConnectionString).UseNodaTime().Build());
				_ = builder.Services.AddKeyedSingleton<NpgsqlDataSource>(
					HistoryDeletionDataSourceKey,
					(_, _) => new NpgsqlDataSourceBuilder(historyDeletionConnectionString).UseNodaTime().Build());
				_ = builder.Services.AddKeyedSingleton<NpgsqlDataSource>(
					CredentialAdministrationDataSourceKey,
					(_, _) => new NpgsqlDataSourceBuilder(credentialAdministrationConnectionString).UseNodaTime().Build());
				_ = builder.Services.AddKeyedSingleton<NpgsqlDataSource>(
					PatManagementDataSourceKey, (_, _) => new NpgsqlDataSourceBuilder(patManagementConnectionString).UseNodaTime().Build());
				_ = builder.Services.AddKeyedSingleton<NpgsqlDataSource>(
					PatAuthenticationDataSourceKey, (_, _) => new NpgsqlDataSourceBuilder(patAuthenticationConnectionString).UseNodaTime().Build());
				_ = builder.Services.AddSingleton<IJobTrackClient>(sp => JobTrackPostgreSql.CreateWithRoleSeparatedDataSources(
					sp.GetRequiredKeyedService<NpgsqlDataSource>(DomainDataSourceKey),
					sp.GetRequiredKeyedService<NpgsqlDataSource>(HistoryDeletionDataSourceKey),
					sp.GetRequiredKeyedService<NpgsqlDataSource>(CredentialAdministrationDataSourceKey),
					sp.GetRequiredKeyedService<NpgsqlDataSource>(PatManagementDataSourceKey),
					sp.GetRequiredKeyedService<NpgsqlDataSource>(PatAuthenticationDataSourceKey),
					clock: sp.GetRequiredService<IClock>(),
					loggerFactory: sp.GetRequiredService<ILoggerFactory>()));
				break;
			case SqliteProviderName:
				// SQLite has no roles/GRANT concept (§2.6 is PostgreSQL-only), so IJobTrackClient
				// keeps sharing ConnectionStrings:JobTrackIdentity's single file with Identity.
				_ = builder.Services.AddSingleton<IJobTrackClient>(sp =>
					JobTrackSqlite.Create(identityConnectionString, clock: sp.GetRequiredService<IClock>()));
				break;
			default:
				throw new InvalidOperationException($"Unknown Database:Provider '{databaseProvider}'.");
		}

		// Bearer requests (the external HTTP API's non-browser CLI consumer, ADR 0029) and cookie
		// requests (the browser) share every /api/* route and its authorization policies -- a policy
		// scheme forwards each request to whichever concrete scheme actually applies to it, rather
		// than picking one scheme globally or duplicating routes per scheme. AddIdentityCookies()
		// returns an IdentityCookiesBuilder, not the outer AuthenticationBuilder, so it cannot be
		// chained directly into AddScheme -- both are called against the original builder instead.
		var authenticationBuilder = builder.Services.AddAuthentication(CookieOrBearerSchemeName)
										   .AddPolicyScheme(CookieOrBearerSchemeName, "Cookie or personal access token", schemeOptions =>
											   schemeOptions.ForwardDefaultSelector = context => PersonalAccessTokenAuthenticationDefaults.IsBearerRequest(context)
												   ? PersonalAccessTokenAuthenticationDefaults.AuthenticationScheme
												   : IdentityConstants.ApplicationScheme);
		_ = authenticationBuilder.AddIdentityCookies();
		_ = authenticationBuilder.AddScheme<AuthenticationSchemeOptions, PersonalAccessTokenAuthenticationHandler>(
			PersonalAccessTokenAuthenticationDefaults.AuthenticationScheme, _ => { });
		_ = builder.Services.AddJobTrackApi();
		var cookieSecurePolicy = builder.Configuration.GetValue<bool>(RequireSecureCookiesConfigKey)
			? CookieSecurePolicy.Always
			: CookieSecurePolicy.SameAsRequest;
		_ = builder.Services.AddAntiforgery(options => {
			options.HeaderName = JobTrackApi.AntiforgeryHeaderName;
			options.Cookie.SecurePolicy = cookieSecurePolicy;
		});
		_ = builder.Services.Configure<CookieTempDataProviderOptions>(options =>
			options.Cookie.SecurePolicy = cookieSecurePolicy);

		// Named, default-deny policies for the six baseline roles (plan §8.3). Coarse admission
		// only -- the library reloads authoritative roles, ownership, and subtree scope itself
		// inside each operation (plan §8.3, spec §7.1) rather than trusting these role claims alone.
		_ = builder.Services.AddAuthorizationBuilder()
				   .AddPolicy(JobTrackPolicyNames.AnyEmployee, policy =>
					   policy.RequireRole(
						   EmployeeRoleNames.Administrator,
						   EmployeeRoleNames.JobManager,
						   EmployeeRoleNames.Worker,
						   EmployeeRoleNames.RateManager,
						   EmployeeRoleNames.CostViewer,
						   EmployeeRoleNames.Auditor))
				   .AddPolicy(JobTrackPolicyNames.JobWorkflow, policy =>
					   policy.RequireRole(
						   EmployeeRoleNames.Administrator,
						   EmployeeRoleNames.JobManager,
						   EmployeeRoleNames.Worker))
				   .AddPolicy(JobTrackPolicyNames.ScheduleAdministration, policy =>
					   policy.RequireRole(
						   EmployeeRoleNames.Administrator,
						   EmployeeRoleNames.Worker))
				   .AddPolicy(JobTrackPolicyNames.RateAdministration, policy =>
					   policy.RequireRole(
						   EmployeeRoleNames.Administrator,
						   EmployeeRoleNames.RateManager,
						   EmployeeRoleNames.CostViewer))
				   .AddPolicy(JobTrackPolicyNames.RateRead, policy =>
					   policy.RequireRole(
						   EmployeeRoleNames.Administrator,
						   EmployeeRoleNames.CostViewer))
				   .AddPolicy(JobTrackPolicyNames.RateWrite, policy =>
					   policy.RequireRole(
						   EmployeeRoleNames.Administrator,
						   EmployeeRoleNames.RateManager))
				   .AddPolicy(JobTrackPolicyNames.AuditSearch, policy =>
					   policy.RequireRole(
						   EmployeeRoleNames.Administrator,
						   EmployeeRoleNames.Auditor))
				   .AddPolicy(JobTrackPolicyNames.RequesterAccess, policy => policy.RequireRole(EmployeeRoleNames.Requester))
				   // Reachable by two disjoint role sets (the request's own Requester, or staff triaging
				   // it) -- RequireAuthorization ANDs every named policy, so this is one combined
				   // coarse-admission policy, not RequesterAccess plus JobWorkflow stacked (ADR 0034). The
				   // authoritative per-request check still lives inside the operation
				   // (RequesterAccessPolicy.CanView/CanCommentAsRequester, JobNodeAccessPolicy.CanManage).
				   .AddPolicy(JobTrackPolicyNames.RequestDetailAccess, policy =>
					   policy.RequireRole(
						   EmployeeRoleNames.Requester,
						   EmployeeRoleNames.Administrator,
						   EmployeeRoleNames.JobManager,
						   EmployeeRoleNames.Worker))
				   // Any signed-in account, employee or Requester, may fetch a CSRF token -- token issuance
				   // itself grants no operational capability; the mutation endpoint each token is later
				   // presented to enforces its own role-scoped policy independently.
				   .AddPolicy(JobTrackPolicyNames.AnyAuthenticatedUser, policy => policy.RequireAuthenticatedUser())
				   .AddPolicy(EmployeeRoleNames.Administrator, policy => policy.RequireRole(EmployeeRoleNames.Administrator))
				   .AddPolicy(EmployeeRoleNames.JobManager, policy => policy.RequireRole(EmployeeRoleNames.JobManager))
				   .AddPolicy(EmployeeRoleNames.Worker, policy => policy.RequireRole(EmployeeRoleNames.Worker))
				   .AddPolicy(EmployeeRoleNames.RateManager, policy => policy.RequireRole(EmployeeRoleNames.RateManager))
				   .AddPolicy(EmployeeRoleNames.CostViewer, policy => policy.RequireRole(EmployeeRoleNames.CostViewer))
				   .AddPolicy(EmployeeRoleNames.Auditor, policy => policy.RequireRole(EmployeeRoleNames.Auditor));

		_ = builder.Services.Configure<IdentityOptions>(options => {
			options.Lockout.MaxFailedAccessAttempts = MaxFailedAccessAttempts;
			options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(LockoutMinutes);
			options.Lockout.AllowedForNewUsers = true;
		});

		_ = builder.Services.ConfigureApplicationCookie(options => {
			options.Cookie.HttpOnly = true;
			options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
			// Lax, not Strict: Strict withholds the auth cookie on every externally-initiated top-level
			// navigation -- a password manager opening the saved URL, an emailed/bookmarked link, and in
			// some browsers the post-login redirect itself -- so an already-signed-in user arrives
			// looking anonymous and is bounced to the login page. Lax still sends the cookie on top-level
			// GET navigations while withholding it from cross-site POSTs; CSRF on state-changing requests
			// is enforced by the antiforgery token (spec §7.1 threat model row 6), not by this cookie's
			// SameSite mode, so relaxing it to Lax does not widen the CSRF surface.
			options.Cookie.SameSite = SameSiteMode.Lax;
			options.ExpireTimeSpan = TimeSpan.FromHours(AuthenticationCookieExpirationHours);
			options.SlidingExpiration = true;
			options.LoginPath = "/Account/Login";
			options.LogoutPath = "/Account/Logout";
			options.AccessDeniedPath = "/Account/AccessDenied";
			options.Events.OnRedirectToLogin = context => JobTrackApi.HandleRedirectAsync(
				context,
				StatusCodes.Status401Unauthorized,
				"Authentication required",
				"/problems/authentication-required");
			options.Events.OnRedirectToAccessDenied = context => JobTrackApi.HandleRedirectAsync(
				context,
				StatusCodes.Status403Forbidden,
				"Forbidden",
				"/problems/authorization-denied");
		});

		// Prompt re-validation of the security stamp on every request (spec §7.1: session
		// revocation on disablement/reset/password change must not wait for the default
		// 30-minute validation interval).
		_ = builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);

		// ADR 0057 (§2.3): AddIdentityCookies() above already points OnValidatePrincipal at
		// SecurityStampValidator.ValidatePrincipalAsync for this named options instance. Configure
		// actions for the same named CookieAuthenticationOptions apply to one shared mutable instance
		// in registration order, so this later Configure call sees that delegate already assigned and
		// wraps it, rather than racing or overwriting it -- run the security stamp check first, then
		// reject the principal (and force sign-out) once the session has outlived the absolute ceiling
		// from its original sign-in (SessionAuthenticationInstants.TryGetOrigin), regardless of how
		// recently SlidingExpiration renewed it. A ticket with no recorded origin (issued before this
		// change shipped) is treated as already expired -- acceptable pre-release, since nothing has
		// shipped yet.
		_ = builder.Services.Configure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options => {
			var validateSecurityStamp = options.Events.OnValidatePrincipal;
			options.Events.OnValidatePrincipal = async context => {
				await validateSecurityStamp(context);
				if (context.Principal is null) {
					return;
				}

				var clock = context.HttpContext.RequestServices.GetRequiredService<IClock>();
				var origin = SessionAuthenticationInstants.TryGetOrigin(context.Properties);
				if (origin is null || clock.GetCurrentInstant() - origin.Value > Duration.FromHours(AuthenticationCookieExpirationHours)) {
					context.RejectPrincipal();
					await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
				}
			};
		});

		var loginRateLimitPermitLimit =
			builder.Configuration.GetValue(LoginRateLimitPermitLimitConfigKey, LoginRateLimitPermitLimit);
		var loginRateLimitWindowSeconds =
			builder.Configuration.GetValue(LoginRateLimitWindowSecondsConfigKey, LoginRateLimitWindowSeconds);
		var apiRateLimitPermitLimit =
			builder.Configuration.GetValue(ApiRateLimitPermitLimitConfigKey, ApiRateLimitPermitLimit);
		var apiRateLimitWindowSeconds =
			builder.Configuration.GetValue(ApiRateLimitWindowSecondsConfigKey, ApiRateLimitWindowSeconds);
		var rateLimitMaxPartitionCount =
			builder.Configuration.GetValue(RateLimitMaxPartitionCountConfigKey, RateLimitMaxPartitionCount);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rateLimitMaxPartitionCount, RateLimitMaxPartitionCountConfigKey);

		var rateLimitingStore = builder.Configuration[RateLimitingStoreConfigKey];
		if (rateLimitingStore is not null && rateLimitingStore != RateLimitingStoreInProcess && rateLimitingStore != RateLimitingStorePostgreSql) {
			throw new InvalidOperationException(
				$"{RateLimitingStoreConfigKey} must be '{RateLimitingStoreInProcess}' or '{RateLimitingStorePostgreSql}' if set.");
		}

		var useSharedRateLimitStore = rateLimitingStore == RateLimitingStorePostgreSql;
		if (useSharedRateLimitStore && databaseProvider != PostgreSqlProviderName) {
			throw new InvalidOperationException(
				$"{RateLimitingStoreConfigKey}={RateLimitingStorePostgreSql} requires Database:Provider={PostgreSqlProviderName}.");
		}

		if (useSharedRateLimitStore) {
			// ADR 0066 Stage 5: PostgreSqlJobTrackIdentityDbContext is request-scoped (AddDbContext),
			// so both limiters register as scoped too; RateLimitMetrics stays a singleton and reaches
			// a scoped context itself only inside its pull-based gauge callback (see its own doc
			// comment), never by holding one across requests.
			_ = builder.Services.AddSingleton<RateLimitMetrics>();
			_ = builder.Services.AddScoped<ILoginAttemptRateLimiter>(sp => new PostgreSqlLoginAttemptRateLimiter(
				sp.GetRequiredService<PostgreSqlJobTrackIdentityDbContext>(),
				sp.GetRequiredService<TimeProvider>(),
				loginRateLimitPermitLimit,
				checked(loginRateLimitPermitLimit * LoginRateLimitBackstopPermitMultiplier),
				TimeSpan.FromSeconds(loginRateLimitWindowSeconds),
				rateLimitMaxPartitionCount,
				sp.GetRequiredService<RateLimitMetrics>()));
			_ = builder.Services.AddScoped<IApiRateLimitStore>(sp => new PostgreSqlApiRateLimitStore(
				sp.GetRequiredService<PostgreSqlJobTrackIdentityDbContext>(),
				sp.GetRequiredService<TimeProvider>(),
				apiRateLimitPermitLimit,
				TimeSpan.FromSeconds(apiRateLimitWindowSeconds),
				rateLimitMaxPartitionCount,
				sp.GetRequiredService<RateLimitMetrics>()));
		} else {
			// In-process limiters: the configured limit effectively multiplies under 2+ instances,
			// since each counts attempts independently. See
			// docs/operations/production-deployment.md's multi-instance in-process-state table.
			// Registered via factory (not a pre-built instance) so the DI container is the one place
			// responsible for disposing their internal state.
			_ = builder.Services.AddSingleton<ILoginAttemptRateLimiter>(_ => new LoginAttemptRateLimiter(
				loginRateLimitPermitLimit, TimeSpan.FromSeconds(loginRateLimitWindowSeconds)));
			_ = builder.Services.AddSingleton<IApiRateLimitStore>(_ =>
				new InProcessApiRateLimitStore(apiRateLimitPermitLimit, TimeSpan.FromSeconds(apiRateLimitWindowSeconds)));
		}

		_ = builder.Services.AddSingleton(TimeProvider.System);
		_ = builder.Services.AddSingleton<ApplicationReadinessState>();
		_ = builder.Services.AddSingleton(sp => new ReadinessProbeGate(
			sp.GetRequiredService<TimeProvider>(), TimeSpan.FromSeconds(ReadinessProbeCacheSeconds)));

		var knownProxies = builder.Configuration.GetSection(ForwardedHeadersKnownProxiesConfigKey).Get<string[]>() ?? [];
		var knownNetworks = builder.Configuration.GetSection(ForwardedHeadersKnownNetworksConfigKey).Get<string[]>() ?? [];
		if (!builder.Environment.IsDevelopment() && knownProxies.Length == 0 && knownNetworks.Length == 0) {
			throw new InvalidOperationException(
				$"{ForwardedHeadersKnownProxiesConfigKey} or {ForwardedHeadersKnownNetworksConfigKey} must list at least one trusted reverse proxy outside Development.");
		}

		_ = builder.Services.Configure<ForwardedHeadersOptions>(options => {
			options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
			options.KnownProxies.Clear();
			options.KnownIPNetworks.Clear();
			foreach (var proxy in knownProxies) {
				options.KnownProxies.Add(IPAddress.Parse(proxy));
			}

			foreach (var network in knownNetworks) {
				options.KnownIPNetworks.Add(IPNetwork.Parse(network));
			}
		});

		var dataProtectionStore = builder.Configuration[DataProtectionStoreConfigKey];
		if (dataProtectionStore is not null
			&& dataProtectionStore != DataProtectionStoreFileSystem
			&& dataProtectionStore != DataProtectionStorePostgreSql) {
			throw new InvalidOperationException(
				$"{DataProtectionStoreConfigKey} must be '{DataProtectionStoreFileSystem}' or '{DataProtectionStorePostgreSql}' if set.");
		}

		var usePostgreSqlDataProtectionStore = dataProtectionStore == DataProtectionStorePostgreSql;
		if (usePostgreSqlDataProtectionStore && databaseProvider != PostgreSqlProviderName) {
			throw new InvalidOperationException(
				$"{DataProtectionStoreConfigKey}={DataProtectionStorePostgreSql} requires Database:Provider={PostgreSqlProviderName}.");
		}

		var dataProtectionKeyPath = builder.Configuration[DataProtectionKeyPathConfigKey];
		if (!usePostgreSqlDataProtectionStore && !builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(dataProtectionKeyPath)) {
			throw new InvalidOperationException(
				$"{DataProtectionKeyPathConfigKey} must be configured outside Development unless " +
				$"{DataProtectionStoreConfigKey}={DataProtectionStorePostgreSql}.");
		}

		if (usePostgreSqlDataProtectionStore || !string.IsNullOrWhiteSpace(dataProtectionKeyPath)) {
			if (!usePostgreSqlDataProtectionStore
				&& !builder.Environment.IsDevelopment()
				&& !Path.IsPathFullyQualified(dataProtectionKeyPath!)) {
				throw new InvalidOperationException($"{DataProtectionKeyPathConfigKey} must be an absolute path outside Development.");
			}

			var dataProtectionBuilder = usePostgreSqlDataProtectionStore
				? builder.Services.AddDataProtection().PersistKeysToDbContext<PostgreSqlJobTrackIdentityDbContext>()
				: builder.Services.AddDataProtection().PersistKeysToFileSystem(new(dataProtectionKeyPath!));
			var certificatePath = builder.Configuration[DataProtectionCertificatePathConfigKey];
			var certificatePasswordPath = builder.Configuration[DataProtectionCertificatePasswordPathConfigKey];
			if (!builder.Environment.IsDevelopment()
				&& (string.IsNullOrWhiteSpace(certificatePath) || !Path.IsPathFullyQualified(certificatePath))) {
				throw new InvalidOperationException(
					$"{DataProtectionCertificatePathConfigKey} must name an absolute PKCS#12 certificate path outside Development.");
			}

			if (!builder.Environment.IsDevelopment()
				&& (string.IsNullOrWhiteSpace(certificatePasswordPath) || !Path.IsPathFullyQualified(certificatePasswordPath))) {
				throw new InvalidOperationException(
					$"{DataProtectionCertificatePasswordPathConfigKey} must name an absolute secret-file path outside Development.");
			}

			if (!string.IsNullOrWhiteSpace(certificatePath) && !string.IsNullOrWhiteSpace(certificatePasswordPath)) {
				var certificatePassword = File.ReadAllText(certificatePasswordPath).TrimEnd('\r', '\n');
				var certificate = X509CertificateLoader.LoadPkcs12FromFile(
					certificatePath,
					certificatePassword);
				_ = dataProtectionBuilder.ProtectKeysWithCertificate(certificate);
			}
		}

		var allowedHostEntries = (builder.Configuration[AllowedHostsConfigKey] ?? string.Empty)
			.Split(AllowedHostsSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (!builder.Environment.IsDevelopment()
			&& (allowedHostEntries.Length == 0 || Array.Exists(allowedHostEntries, host => host == AllowedHostsCatchAll))) {
			throw new InvalidOperationException(
				$"{AllowedHostsConfigKey} must list this deployment's own host names outside Development; '{AllowedHostsCatchAll}' disables host filtering entirely.");
		}

		_ = builder.Services.AddCors(options => options.AddPolicy(CorsPolicyName, policy => policy.WithOrigins()));

		_ = builder.Services.AddRequestTimeouts(options =>
			options.DefaultPolicy = new() {
				Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds),
			});

		// Kestrel-level defense in depth; the enforced, testable limit is the middleware below --
		// WebApplicationFactory's TestServer never exercises Kestrel's own body-size enforcement,
		// so this line has no in-process test coverage (see docs/operations/web-host-security.md).
		_ = builder.WebHost.ConfigureKestrel(options => {
			options.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
			options.Limits.MinRequestBodyDataRate = new(
				MinRequestBodyDataRateBytesPerSecond,
				TimeSpan.FromSeconds(MinRequestBodyDataRateGracePeriodSeconds));
		});

		var deploymentTopology = builder.Configuration[DeploymentTopologyConfigKey];
		if (deploymentTopology is not null
			&& deploymentTopology != DeploymentTopologySingleInstance
			&& deploymentTopology != DeploymentTopologyMultiInstance) {
			throw new InvalidOperationException(
				$"{DeploymentTopologyConfigKey} must be '{DeploymentTopologySingleInstance}' or '{DeploymentTopologyMultiInstance}' if set.");
		}

		if (deploymentTopology == DeploymentTopologyMultiInstance) {
			if (databaseProvider != PostgreSqlProviderName) {
				throw new InvalidOperationException(
					$"{DeploymentTopologyConfigKey}={DeploymentTopologyMultiInstance} requires Database:Provider={PostgreSqlProviderName}.");
			}

			if (!usePostgreSqlDataProtectionStore) {
				throw new InvalidOperationException(
					$"{DeploymentTopologyConfigKey}={DeploymentTopologyMultiInstance} requires {DataProtectionStoreConfigKey}={DataProtectionStorePostgreSql}.");
			}

			if (!useSharedRateLimitStore) {
				throw new InvalidOperationException(
					$"{DeploymentTopologyConfigKey}={DeploymentTopologyMultiInstance} requires {RateLimitingStoreConfigKey}={RateLimitingStorePostgreSql}.");
			}
		}

		var app = builder.Build();

		// Stage 6: flip readiness to draining as soon as shutdown begins, not when the process
		// actually exits -- ApplicationStopping fires before Kestrel stops accepting connections,
		// giving an orchestrator a window to observe /health/ready failing and stop routing new
		// requests here while in-flight ones finish.
		var readinessState = app.Services.GetRequiredService<ApplicationReadinessState>();
		_ = app.Lifetime.ApplicationStopping.Register(readinessState.BeginDraining);

		// Forwarded-header trust boundary comes first: it must run before anything (HTTPS
		// redirection, rate limiting by remote IP) that reads the scheme or client address.
		_ = app.UseForwardedHeaders();

		// Configure the HTTP request pipeline.
		if (!app.Environment.IsDevelopment()) {
			// StatusCodeSelector: without it, ExceptionHandlerMiddleware forces every unhandled
			// exception -- including Kestrel's own BadHttpRequestException for a body exceeding
			// MaxRequestBodySize mid-read -- to 500, misreporting a legitimate client-side rejection
			// as a server fault. BadHttpRequestException carries the status code Kestrel itself would
			// have used (400/413) had the exception not been intercepted here first. It surfaces one
			// level down the chain, not as the top-level exception: antiforgery validation reads the
			// request form to locate the token, so an oversized POST throws
			// AntiforgeryValidationException with the BadHttpRequestException as its InnerException.
			_ = app.UseExceptionHandler(new ExceptionHandlerOptions {
				ExceptionHandlingPath = "/Error",
				StatusCodeSelector = static exception => exception switch {
					BadHttpRequestException badHttpRequestException => badHttpRequestException.StatusCode,
															 { InnerException: BadHttpRequestException innerBadHttpRequestException } =>
																 innerBadHttpRequestException.StatusCode,
					_ => StatusCodes.Status500InternalServerError,
				},
			});
			// The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
			_ = app.UseHsts();
		}

		_ = app.UseHttpsRedirection();

		_ = app.Use(async (context, next) => {
			context.Response.Headers[ContentSecurityPolicyHeaderName] = ContentSecurityPolicy;
			context.Response.Headers[ContentTypeOptionsHeaderName] = ContentTypeOptionsHeaderValue;
			context.Response.Headers[FrameOptionsHeaderName] = FrameOptionsHeaderValue;
			context.Response.Headers[ReferrerPolicyHeaderName] = ReferrerPolicyHeaderValue;

			// Dynamic pages must never be replayed from the browser cache or back-forward cache
			// after logout or a role/permission change. Registered via OnStarting (fired just
			// before headers are sent, after routing and endpoint execution) rather than set
			// eagerly here, so a fingerprinted static asset from MapStaticAssets -- which sets its
			// own long-lived, immutable Cache-Control before this callback runs -- is left alone.
			context.Response.OnStarting(() => {
				if (!context.Response.Headers.ContainsKey(CacheControlHeaderName)) {
					context.Response.Headers[CacheControlHeaderName] = CacheControlHeaderValue;
					context.Response.Headers[PragmaHeaderName] = PragmaHeaderValue;
				}

				return Task.CompletedTask;
			});

			await next(context);
		});

		_ = app.Use(async (context, next) => {
			if (context.Request.ContentLength is long contentLength && contentLength > MaxRequestBodyBytes) {
				context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
				await context.Response.WriteAsJsonAsync(
					new ProblemDetails {
						Status = StatusCodes.Status413PayloadTooLarge,
						Title = "Payload too large",
						Detail = $"Request bodies are limited to {MaxRequestBodyBytes} bytes.",
						Type = RequestTooLargeProblemType,
					},
					options: null,
					contentType: "application/problem+json");
				return;
			}

			var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
			if (bodySizeFeature is { IsReadOnly: false }) {
				bodySizeFeature.MaxRequestBodySize = MaxRequestBodyBytes;
			}

			await next(context);
		});

		_ = app.UseRouting();

		_ = app.UseCors(CorsPolicyName);

		// Authentication must run before rate limiting: the external API's per-user partition key
		// reads the authenticated principal's name, which does not exist yet if this runs first --
		// every caller would otherwise fall back to the same remote-address partition regardless of
		// which user they are.
		_ = app.UseAuthentication();

		// A plain middleware, not an endpoint filter (ADR 0066 Stage 5): an endpoint filter runs only
		// after UseAuthorization() lets a request through to its endpoint, so it could never limit a
		// caller authorization was always going to reject anyway -- this must sit exactly where the
		// framework's own UseRateLimiter() used to, between authentication and authorization.
		_ = app.Use(async (context, next) => {
			if (!context.Request.Path.StartsWithSegments(JobTrackApi.ApiPathPrefix)) {
				await next(context);
				return;
			}

			var store = context.RequestServices.GetRequiredService<IApiRateLimitStore>();
			var partitionKey = context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
			var outcome = await store.TryAcquireAsync(partitionKey, context.RequestAborted);
			switch (outcome) {
				case RateLimitOutcome.Allowed:
					await next(context);
					break;
				case RateLimitOutcome.Denied:
					await WriteRateLimitProblemAsync(
						context, StatusCodes.Status429TooManyRequests, "Too many requests",
						"Rate limit exceeded. Retry after the current window elapses.", RateLimitedProblemType);
					break;
				case RateLimitOutcome.StoreUnavailable:
					await WriteRateLimitProblemAsync(
						context, StatusCodes.Status503ServiceUnavailable, "Rate limit store unavailable",
						"The shared rate-limit store could not be reached. Retry shortly.", RateLimitStoreUnavailableProblemType);
					break;
				default:
					throw new UnreachableException($"Unknown rate-limit outcome: {outcome}.");
			}
		});

		_ = app.UseAuthorization();
		_ = app.UseAntiforgery();
		_ = app.UseRequestTimeouts();

		// Stage 6: process-only liveness -- no dependency check, so a database outage never makes
		// Cloud Run kill and replace an otherwise-healthy instance. Anonymous and outside the
		// /api-prefixed rate-limiting middleware above (structural exemption, not a per-route opt
		// out) -- see WebHostSecurityArchitectureTests for the closed anonymous-endpoint inventory.
		_ = app.MapGet("/health/live", () => Results.Ok()).AllowAnonymous().ExcludeFromDescription();

		// Stage 6: readiness -- verifies the domain database and the Identity/data-protection
		// repository are usable. Draining (set on ApplicationStopping, above) is checked first and
		// short-circuits the dependency probes entirely, since a shutting-down instance must stop
		// receiving traffic regardless of whether its dependencies still answer. Every failure path
		// returns a bare status code with no body: neither the caught exception nor
		// ApplicationVersion is ever written to the response, matching plan §2.6's "no exception or
		// version detail".
		_ = app.MapGet("/health/ready", async (HttpContext context) => {
			if (!readinessState.IsAcceptingTraffic) {
				return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
			}

			var probeGate = context.RequestServices.GetRequiredService<ReadinessProbeGate>();
			var isReady = await probeGate.CheckAsync(async cancellationToken => {
				try {
					// EF Core's CanConnectAsync() is unreliable for this purpose on the Sqlite provider
					// used by SingleInstance/demo topologies (observed returning false against a
					// reachable, schema-deployed database) -- a direct open/close proves connectivity for
					// both providers without depending on that behaviour.
					var identityDbContext = context.RequestServices.GetRequiredService<JobTrackIdentityDbContext>();
					await identityDbContext.Database.OpenConnectionAsync(cancellationToken);
					await identityDbContext.Database.CloseConnectionAsync();

					if (databaseProvider == PostgreSqlProviderName) {
						var domainDataSource = context.RequestServices.GetRequiredKeyedService<NpgsqlDataSource>(DomainDataSourceKey);
						await using var connection = domainDataSource.CreateConnection();
						await connection.OpenAsync(cancellationToken);
					}

					return true;
				}
				catch (Exception ex) when (ex is NpgsqlException or SqliteException or TimeoutException or InvalidOperationException) {
					return false;
				}
			}, context.RequestAborted);

			return isReady ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
		}).AllowAnonymous().ExcludeFromDescription();

		_ = app.MapStaticAssets();
		app.MapJobTrackApi();
		_ = app.MapRazorPages()
			   .WithStaticAssets();

		app.Run();
	}

	private static async Task WriteRateLimitProblemAsync(HttpContext context, int statusCode, string title, string detail, string problemType)
	{
		context.Response.StatusCode = statusCode;
		context.Response.ContentType = "application/problem+json";
		await context.Response.WriteAsJsonAsync(
			new ProblemDetails {
				Status = statusCode,
				Title = title,
				Detail = detail,
				Type = problemType,
			},
			options: null,
			contentType: "application/problem+json",
			cancellationToken: context.RequestAborted);
	}
}
