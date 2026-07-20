namespace JobTrack.Web;

using System.Net;
using System.Threading.RateLimiting;
using Application;
using Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
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
	private const string ApiRateLimitPermitLimitConfigKey = "RateLimiting:ApiPermitLimit";
	private const string ApiRateLimitWindowSecondsConfigKey = "RateLimiting:ApiWindowSeconds";
	private const string RateLimitedProblemType = "/problems/rate-limited";
	private const int MaxFailedAccessAttempts = 5;
	private const int LockoutMinutes = 15;
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

	// Plan §8.2: data-protection keys persisted outside the application directory. Outside
	// Development, an unconfigured path fails startup closed rather than falling back to the
	// framework's ephemeral/registry-based default key ring.
	private const string DataProtectionKeyPathConfigKey = "DataProtection:KeyPath";

	// No attachments/file uploads exist in this content model (fix-plan non-goals), so request
	// bodies are plain JSON/form payloads -- generous headroom over the largest legitimate body
	// (a schedule version with a full week of intervals) without leaving the limit effectively
	// unbounded.
	private const long MaxRequestBodyBytes = 64 * 1024;
	private const string RequestTooLargeProblemType = "/problems/request-too-large";

	private const int RequestTimeoutSeconds = 30;

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

	private Program()
	{
	}

	public static void Main(string[] args)
	{
		var builder = WebApplication.CreateBuilder(args);

		// Add services to the container.
		_ = builder.Services.AddRazorPages(options =>
			options.Conventions.AddFolderApplicationModelConvention("/", model =>
				model.Filters.Add(new RequiresPasswordChangePageFilter())));

		_ = builder.Services.AddScoped<IViewerTimeZoneResolver, ViewerTimeZoneResolver>();
		_ = builder.Services.AddSingleton<IClock>(SystemClock.Instance);
		_ = builder.Services.AddSingleton(sp => new PendingPatDeliveryStore(sp.GetRequiredService<IClock>()));

		var databaseProvider = builder.Configuration["Database:Provider"]
							   ?? throw new InvalidOperationException("Database:Provider is not configured.");
		var identityConnectionString = builder.Configuration.GetConnectionString("JobTrackIdentity")
									   ?? throw new InvalidOperationException("ConnectionStrings:JobTrackIdentity is not configured.");

		var identityBuilder = databaseProvider switch {
			PostgreSqlProviderName => builder.Services.AddJobTrackIdentityPostgreSql(identityConnectionString),
			SqliteProviderName => builder.Services.AddJobTrackIdentitySqlite(identityConnectionString),
			_ => throw new InvalidOperationException($"Unknown Database:Provider '{databaseProvider}'."),
		};
		_ = identityBuilder.AddSignInManager<JobTrackSignInManager>();

		// app_user, identity_user, and identity_role live in the same schema/database (database
		// schema version 0002) as the rest of the domain data, so IJobTrackClient shares
		// ConnectionStrings:JobTrackIdentity rather than needing a second connection string.
		switch (databaseProvider) {
			case PostgreSqlProviderName:
				_ = builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(identityConnectionString).UseNodaTime().Build());
				_ = builder.Services.AddSingleton<IJobTrackClient>(sp => JobTrackPostgreSql.Create(
					sp.GetRequiredService<NpgsqlDataSource>(), clock: sp.GetRequiredService<IClock>()));
				break;
			case SqliteProviderName:
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
		_ = builder.Services.AddAntiforgery(options => options.HeaderName = JobTrackApi.AntiforgeryHeaderName);

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
			options.Cookie.SameSite = SameSiteMode.Strict;
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

		var loginRateLimitPermitLimit =
			builder.Configuration.GetValue(LoginRateLimitPermitLimitConfigKey, LoginRateLimitPermitLimit);
		var loginRateLimitWindowSeconds =
			builder.Configuration.GetValue(LoginRateLimitWindowSecondsConfigKey, LoginRateLimitWindowSeconds);

		_ = builder.Services.AddSingleton(new LoginAttemptRateLimiter(
			loginRateLimitPermitLimit,
			TimeSpan.FromSeconds(loginRateLimitWindowSeconds)));

		var apiRateLimitPermitLimit =
			builder.Configuration.GetValue(ApiRateLimitPermitLimitConfigKey, ApiRateLimitPermitLimit);
		var apiRateLimitWindowSeconds =
			builder.Configuration.GetValue(ApiRateLimitWindowSecondsConfigKey, ApiRateLimitWindowSeconds);

		_ = builder.Services.AddRateLimiter(options => {
			_ = options.AddPolicy(JobTrackApi.RateLimiterPolicyName, httpContext =>
				RateLimitPartition.GetFixedWindowLimiter(
					httpContext.User.Identity?.Name ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
					_ => new() { PermitLimit = apiRateLimitPermitLimit, Window = TimeSpan.FromSeconds(apiRateLimitWindowSeconds), QueueLimit = 0 }));
			options.OnRejected = async (context, cancellationToken) => {
				context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
				context.HttpContext.Response.ContentType = "application/problem+json";
				await context.HttpContext.Response.WriteAsJsonAsync(
					new ProblemDetails {
						Status = StatusCodes.Status429TooManyRequests,
						Title = "Too many requests",
						Detail = "Rate limit exceeded. Retry after the current window elapses.",
						Type = RateLimitedProblemType,
					},
					options: null,
					contentType: "application/problem+json",
					cancellationToken: cancellationToken);
			};
		});

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

		var dataProtectionKeyPath = builder.Configuration[DataProtectionKeyPathConfigKey];
		if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(dataProtectionKeyPath)) {
			throw new InvalidOperationException($"{DataProtectionKeyPathConfigKey} must be configured outside Development.");
		}

		if (!string.IsNullOrWhiteSpace(dataProtectionKeyPath)) {
			_ = builder.Services.AddDataProtection().PersistKeysToFileSystem(new(dataProtectionKeyPath));
		}

		_ = builder.Services.AddCors(options => options.AddPolicy(CorsPolicyName, policy => policy.WithOrigins()));

		_ = builder.Services.AddRequestTimeouts(options =>
			options.DefaultPolicy = new() { Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds) });

		// Kestrel-level defense in depth; the enforced, testable limit is the middleware below --
		// WebApplicationFactory's TestServer never exercises Kestrel's own body-size enforcement,
		// so this line has no in-process test coverage (see docs/operations/web-host-security.md).
		_ = builder.WebHost.ConfigureKestrel(options => {
			options.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
			options.Limits.MinRequestBodyDataRate = new(
				MinRequestBodyDataRateBytesPerSecond,
				TimeSpan.FromSeconds(MinRequestBodyDataRateGracePeriodSeconds));
		});

		var app = builder.Build();

		// Forwarded-header trust boundary comes first: it must run before anything (HTTPS
		// redirection, rate limiting by remote IP) that reads the scheme or client address.
		_ = app.UseForwardedHeaders();

		// Configure the HTTP request pipeline.
		if (!app.Environment.IsDevelopment()) {
			_ = app.UseExceptionHandler("/Error");
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
		// (JobTrackApi.RateLimiterPolicyName) reads the authenticated principal's name, which does
		// not exist yet if this runs first -- every caller would otherwise fall back to the same
		// remote-address partition regardless of which user they are.
		_ = app.UseAuthentication();

		_ = app.UseRateLimiter();

		_ = app.UseAuthorization();
		_ = app.UseAntiforgery();
		_ = app.UseRequestTimeouts();

		_ = app.MapStaticAssets();
		app.MapJobTrackApi();
		_ = app.MapRazorPages()
			.WithStaticAssets();

		app.Run();
	}
}
