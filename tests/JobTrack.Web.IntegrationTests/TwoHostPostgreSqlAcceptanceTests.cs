namespace JobTrack.Web.IntegrationTests;

using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using AwesomeAssertions;
using Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using NodaTime;
using Npgsql;
using Persistence.PostgreSql;
using TestSupport;
using Program = Program;

/// <summary>
///     Stage 1 of <c>docs/plans/2026-07-26-multi-instance-web-deployment-plan.md</c>: a reusable
///     two-host PostgreSQL fixture proving today's genuine cross-instance defects before their Stage
///     2-6 remediation lands. Host A and host B are independent
///     <see cref="WebApplicationFactory{TEntryPoint}" /> instances -- separate DI containers/service
///     providers and, crucially, separate <c>DataProtection:KeyPath</c> temp directories, so each
///     behaves like a genuinely separate container instance rather than accidentally sharing
///     <c>Program.cs</c>'s Development-mode default key ring (which is keyed by content root path and
///     would otherwise be identical for two factories built from the same test assembly) -- both
///     point at the same schema-deployed PostgreSQL database. Every test uses direct host clients,
///     never a shared cookie jar or load balancer (plan Stage 1: "Direct host clients make routing
///     deterministic; do not depend on a probabilistic load balancer").
/// </summary>
public sealed partial class TwoHostPostgreSqlAcceptanceTests : IAsyncLifetime, IDisposable
{
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string AntiforgeryHeaderName = "X-CSRF-TOKEN";
	private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const int TotpDigits = 6;
	private const int TotpStepSeconds = 30;

	private static readonly FrozenDictionary<string, string?> PostgreSqlDataProtectionStoreSetting =
		new Dictionary<string, string?> { ["DataProtection:Store"] = "PostgreSql" }.ToFrozenDictionary();

	private readonly PostgreSqlDatabaseFixture database = new();

	private readonly List<string> temporaryDirectories = [];
	private AppUserId administratorId;
	private HttpClient clientA = null!;
	private HttpClient clientB = null!;
	private TestWebApplicationFactory hostA = null!;
	private TestWebApplicationFactory hostB = null!;
	private JobNodeId rootId;
	private IJobTrackClient seedClient = null!;
	private NpgsqlDataSource? seedDataSource;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await DeploySchemaAsync();

		seedDataSource = new NpgsqlDataSourceBuilder(database.ConnectionString).UseNodaTime().Build();
		seedClient = JobTrackPostgreSql.Create(seedDataSource);
		var bootstrap = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.two-host",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		administratorId = bootstrap.AdministratorId;
		rootId = bootstrap.RootJobNodeId;
		await ClearRequiresPasswordChangeAsync();

		// ADR 0066 Stage 2: both hosts now share the PostgreSQL data-protection key repository
		// instead of each getting its own filesystem key ring, which is what makes an
		// authentication cookie, antiforgery token, or TOTP secret minted on one host valid on the
		// other. Filter memory, the rate limiters, and PAT delivery are unaffected -- their own
		// Stage 3-5 remediations have not landed yet.
		hostA = new(database.ConnectionString, CreateTemporaryDirectory("host-a"), PostgreSqlDataProtectionStoreSetting);
		hostB = new(database.ConnectionString, CreateTemporaryDirectory("host-b"), PostgreSqlDataProtectionStoreSetting);
		clientA = hostA.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
		clientB = hostB.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
	}

	public async Task DisposeAsync()
	{
		Dispose();

		if (seedDataSource is not null) {
			await seedDataSource.DisposeAsync();
		}

		await database.DisposeAsync();
	}

	public void Dispose()
	{
		clientA.Dispose();
		clientB.Dispose();
		hostA.Dispose();
		hostB.Dispose();

		foreach (var path in temporaryDirectories) {
			if (Directory.Exists(path)) {
				Directory.Delete(path, true);
			}
		}
	}

	[Fact]
	public async Task An_authentication_cookie_minted_by_host_A_is_rejected_by_host_B()
	{
		_ = await CreateEmployeeAsync("cookie.crosshost");

		var authCookieA = await SignInAsync(clientA, "cookie.crosshost");

		using var request = new HttpRequestMessage(HttpMethod.Get, "/Jobs/AwaitingProgress");
		request.Headers.Add("Cookie", authCookieA);
		var responseFromB = await clientB.SendAsync(request);

		// Target (post-Stage-2, shared PostgreSQL key ring): host B decrypts A's authentication
		// ticket and serves the page. Today: separate filesystem key rings mean B cannot validate
		// A's cookie, so the request is treated as anonymous and redirected to the login page.
		responseFromB.StatusCode.Should().Be(HttpStatusCode.OK, "a request authenticated on host A must remain authenticated when it reaches host B");
	}

	[Fact]
	public async Task An_antiforgery_token_issued_by_host_A_does_not_validate_a_POST_on_host_B()
	{
		_ = await CreateEmployeeAsync("antiforgery.crosshost");
		var leafId = await AddUnassignedLeafAsync("Antiforgery cross-host leaf");
		var authCookieA = await SignInAsync(clientA, "antiforgery.crosshost");
		var (antiforgeryCookieA, tokenA) = await GetAntiforgeryTokenAsync(clientA, authCookieA);

		// Authenticate independently on B (its own valid cookie) so this test isolates the
		// antiforgery-key defect from the authentication-cookie defect covered above.
		var authCookieB = await SignInAsync(clientB, "antiforgery.crosshost");

		using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{leafId.Value}/pickup");
		request.Headers.Add("Cookie", $"{authCookieB}; {antiforgeryCookieA}");
		request.Headers.Add(AntiforgeryHeaderName, tokenA);
		var response = await clientB.SendAsync(request);

		// Target (post-Stage-2): a token/cookie pair minted by A validates on B via the shared key
		// ring. Today: B cannot decrypt A's antiforgery cookie, so validation fails closed (400).
		response.StatusCode.Should().Be(HttpStatusCode.OK, "an antiforgery token/cookie pair issued by host A must validate a POST served by host B");
	}

	[Fact]
	public async Task A_TOTP_secret_enrolled_through_host_A_cannot_be_validated_by_host_B()
	{
		_ = await CreateEmployeeAsync("totp.crosshost");
		var authCookieA = await SignInAsync(clientA, "totp.crosshost");

		var (secret, refreshedAuthCookieA, antiforgeryCookieA, tokenA) = await GetEnrolmentFormAsync(clientA, authCookieA);
		var enrolCode = GenerateTotpCode(secret, DateTimeOffset.UtcNow);
		var confirmResponse = await PostConfirmAsync(clientA, refreshedAuthCookieA, antiforgeryCookieA, tokenA, enrolCode);
		confirmResponse.StatusCode.Should()
			.Be(HttpStatusCode.Redirect, "the enrolment step itself must succeed on host A before the cross-host check is meaningful");

		var loginResponse = await PostLoginAsync(clientB, "totp.crosshost", KnownPassword);

		// Target (post-Stage-2): host B decrypts the AuthenticatorKeyProtected value host A wrote,
		// via the shared PostgreSQL key ring, and challenges normally (redirect to
		// /Account/LoginTwoFactor). Today, JobTrackUserStore's per-host IDataProtector cannot
		// Unprotect ciphertext produced by a different key ring; SignInManager's own
		// IsTwoFactorEnabledAsync check surfaces that as an unhandled CryptographicException from
		// the very first password-check step, which Development's DeveloperExceptionPageMiddleware
		// turns into a 500 problem response rather than a redirect.
		loginResponse.StatusCode.Should().Be(HttpStatusCode.Redirect, "host B must be able to challenge for a TOTP code once it can decrypt A's key");
		loginResponse.Headers.Location?.OriginalString.Should().Contain("/Account/LoginTwoFactor");
	}

	[Fact]
	public async Task AwaitingProgress_filters_set_on_host_A_are_not_recalled_by_host_B_though_sign_out_sign_in_still_clears_them()
	{
		var workerId = await CreateEmployeeAsync("filtermem.worker");
		var otherWorkerId = await CreateEmployeeAsync("filtermem.other");
		_ = await AddLeafWithWorkAsync(null, "Filter pool marker");
		_ = await AddLeafWithWorkAsync(workerId, "Filter owned marker");
		_ = await AddLeafWithWorkAsync(otherWorkerId, "Filter other marker");

		var authCookieA = await SignInAsync(clientA, "filtermem.worker");
		var authCookieB = await SignInAsync(clientB, "filtermem.worker");

		// Free-text shape.
		var textSessionCookie = await EstablishSessionCookieAsync(clientA, authCookieA, "?searchText=Filter+pool+marker");
		var textBodyOnB = await GetAwaitingProgressBodyAsync(clientB, authCookieB, textSessionCookie);
		textBodyOnB.Should().Contain("Filter pool marker", "host B should recall A's free-text filter");
		textBodyOnB.Should().NotContain("Filter owned marker");

		// Checkbox flag shape.
		var flagSessionCookie = await EstablishSessionCookieAsync(clientA, authCookieA, "?unassignedOnly=true");
		var flagBodyOnB = await GetAwaitingProgressBodyAsync(clientB, authCookieB, flagSessionCookie);
		flagBodyOnB.Should().Contain("Filter pool marker", "host B should recall A's unassigned-only filter");
		flagBodyOnB.Should().NotContain("Filter owned marker");

		// Id filter shape.
		var idSessionCookie = await EstablishSessionCookieAsync(clientA, authCookieA, $"?ownerUserId={workerId.Value}");
		var idBodyOnB = await GetAwaitingProgressBodyAsync(clientB, authCookieB, idSessionCookie);
		idBodyOnB.Should().Contain("Filter owned marker", "host B should recall A's owner filter");
		idBodyOnB.Should().NotContain("Filter pool marker");
		idBodyOnB.Should().NotContain("Filter other marker");

		// Reset boundary, same host throughout -- expected to already work today (plan §2.1/§2.3).
		var (logoutAntiforgeryCookie, logoutToken) = await GetFormAsync(clientA, "/Account/Logout", $"{authCookieA}; {idSessionCookie}");
		using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/Logout");
		logoutRequest.Headers.Add("Cookie", $"{authCookieA}; {idSessionCookie}; {logoutAntiforgeryCookie}");
		logoutRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = logoutToken });
		_ = await clientA.SendAsync(logoutRequest);

		// The filter cookie's state now travels in the cookie value itself (ADR 0066 Stage 3), rather
		// than a session id pointing at server-side state, so the reset boundary works by *deleting*
		// the cookie -- Persisting_a_value/Clear_deletes_the_cookie in CookieFilterMemoryStoreTests
		// pin that directly. A compliant client honors that Set-Cookie deletion and stops sending
		// idSessionCookie, exactly as this final request does not carry it.
		var reauthCookieA = await SignInAsync(clientA, "filtermem.worker");
		using var reauthRequest = new HttpRequestMessage(HttpMethod.Get, "/Jobs/AwaitingProgress");
		reauthRequest.Headers.Add("Cookie", reauthCookieA);
		var reauthResponse = await clientA.SendAsync(reauthRequest);
		reauthResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		var bodyAfterReauth = await reauthResponse.Content.ReadAsStringAsync();
		bodyAfterReauth.Should().Contain("Filter pool marker", "sign-out/sign-in must clear the remembered owner filter");
		bodyAfterReauth.Should().Contain("Filter owned marker");
		bodyAfterReauth.Should().Contain("Filter other marker");
	}

	[Fact]
	public async Task Login_permits_consumed_alternately_across_hosts_A_and_B_do_not_exceed_the_configured_global_limit()
	{
		const int PermitLimit = 2;
		const int AttemptCount = 5;
		_ = await CreateEmployeeAsync("ratelimit.login");

		using var limitedHostA = new TestWebApplicationFactory(
			database.ConnectionString, CreateTemporaryDirectory("rl-a"), LoginPermitLimitSetting(PermitLimit));
		using var limitedHostB = new TestWebApplicationFactory(
			database.ConnectionString, CreateTemporaryDirectory("rl-b"), LoginPermitLimitSetting(PermitLimit));
		using var limitedClientA = limitedHostA.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
		using var limitedClientB = limitedHostB.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });

		var successCount = 0;
		for (var attempt = 0; attempt < AttemptCount; ++attempt) {
			var client = attempt % 2 == 0 ? limitedClientA : limitedClientB;
			var (antiforgeryCookie, token) = await GetLoginFormAsync(client);
			using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
			request.Headers.Add("Cookie", antiforgeryCookie);
			request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
				["Input.UserName"] = "ratelimit.login",
				["Input.Password"] = "deliberately-wrong-password",
				["__RequestVerificationToken"] = token,
			});
			var response = await client.SendAsync(request);
			if (response.StatusCode == HttpStatusCode.TooManyRequests) {
				break;
			}

			++successCount;
		}

		// Target (post-Stage-5, shared PostgreSQL counter): exactly PermitLimit attempts succeed
		// globally before a 429, regardless of which host served each request. Today: each host's
		// LoginAttemptRateLimiter is an independent in-process counter, so alternating hosts admits
		// up to PermitLimit attempts *per host* before either one starts rejecting.
		successCount.Should().Be(PermitLimit, "the login limit must be global across hosts, not per host");
	}

	[Fact]
	public async Task Api_permits_consumed_alternately_across_hosts_A_and_B_do_not_exceed_the_configured_global_limit()
	{
		const int PermitLimit = 2;
		const int AttemptCount = 5;
		var workerId = await CreateEmployeeAsync("ratelimit.api");
		var issued = await seedClient.Tokens.IssueAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			TargetUserId = workerId,
			Label = "rate-limit-test-token",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});

		using var limitedHostA = new TestWebApplicationFactory(
			database.ConnectionString, CreateTemporaryDirectory("api-rl-a"), ApiPermitLimitSetting(PermitLimit));
		using var limitedHostB = new TestWebApplicationFactory(
			database.ConnectionString, CreateTemporaryDirectory("api-rl-b"), ApiPermitLimitSetting(PermitLimit));
		using var limitedClientA = limitedHostA.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
		using var limitedClientB = limitedHostB.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });

		var successCount = 0;
		HttpResponseMessage? lastResponse = null;
		for (var attempt = 0; attempt < AttemptCount; ++attempt) {
			var client = attempt % 2 == 0 ? limitedClientA : limitedClientB;
			using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/employees/{workerId.Value}/rates");
			request.Headers.Authorization = new("Bearer", issued.Token);
			lastResponse = await client.SendAsync(request);
			if (lastResponse.StatusCode == HttpStatusCode.TooManyRequests) {
				break;
			}

			++successCount;
		}

		// Target (post-Stage-5): exactly PermitLimit requests succeed globally before a 429. Today:
		// ASP.NET Core's GetFixedWindowLimiter partitions are in-process, same as the login limiter.
		successCount.Should().Be(PermitLimit, "the API rate limit must be global across hosts, not per host");
		lastResponse!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the existing 429 contract must still apply once the true limit is hit");
		lastResponse.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task A_PAT_issued_through_host_A_cannot_be_revealed_by_the_redirected_GET_on_host_B()
	{
		_ = await CreateEmployeeAsync("pat.crosshost");
		var authCookieA = await SignInAsync(clientA, "pat.crosshost");
		var authCookieB = await SignInAsync(clientB, "pat.crosshost");

		var (antiforgeryCookieA, tokenA) = await GetFormAsync(clientA, "/Account/PersonalAccessTokens", authCookieA);
		using var issueRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/PersonalAccessTokens?handler=Issue");
		issueRequest.Headers.Add("Cookie", $"{authCookieA}; {antiforgeryCookieA}");
		issueRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Issue.Label"] = "cross-host-token",
			["Issue.LifetimeDays"] = "30",
			["__RequestVerificationToken"] = tokenA,
		});
		var issueResponse = await clientA.SendAsync(issueRequest);
		issueResponse.StatusCode.Should().Be(HttpStatusCode.Redirect, "PRG: a successful issuance never renders the token directly");
		var deliveryCookie = WebTestHttp.ExtractCookiePair(
			WebTestHttp.FindSetCookie(issueResponse, "JobTrack.PendingPat") ?? throw new InvalidOperationException("No pending-PAT delivery cookie was set."));

		using var revealRequest = new HttpRequestMessage(HttpMethod.Get, issueResponse.Headers.Location);
		revealRequest.Headers.Add("Cookie", $"{authCookieB}; {deliveryCookie}");
		var revealResponseFromB = await clientB.SendAsync(revealRequest);
		var revealBodyFromB = await revealResponseFromB.Content.ReadAsStringAsync();

		// Target (post-Stage-4, protected delivery cookie): host B, authenticated independently as
		// the same actor, can still complete the redirected GET and reveal the plaintext exactly
		// once, decrypting the cookie host A's POST response set via the shared PostgreSQL key ring
		// (Stage 2). Today: PendingPatDeliveryStore is an in-process Dictionary on host A, so host B
		// has no record of the reservation and reports it unavailable.
		revealResponseFromB.StatusCode.Should().Be(HttpStatusCode.OK);
		revealBodyFromB.Should().Contain("jtpat_", "host B should be able to display the token host A issued");
	}

	[Fact]
	public async Task A_concurrent_pickup_race_between_host_A_and_host_B_produces_exactly_one_winner()
	{
		var workerAId = await CreateEmployeeAsync("race.workerA");
		var workerBId = await CreateEmployeeAsync("race.workerB");
		var leafId = await AddUnassignedLeafAsync("Race leaf");
		var tokenA = await seedClient.Tokens.IssueAsync(new() {
			Context = new() { Actor = workerAId, CorrelationId = Guid.NewGuid() },
			TargetUserId = workerAId,
			Label = "race-a",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});
		var tokenB = await seedClient.Tokens.IssueAsync(new() {
			Context = new() { Actor = workerBId, CorrelationId = Guid.NewGuid() },
			TargetUserId = workerBId,
			Label = "race-b",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});

		using var requestA = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{leafId.Value}/pickup");
		requestA.Headers.Authorization = new("Bearer", tokenA.Token);
		using var requestB = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{leafId.Value}/pickup");
		requestB.Headers.Authorization = new("Bearer", tokenB.Token);

		var responseATask = clientA.SendAsync(requestA);
		var responseBTask = clientB.SendAsync(requestB);
		await Task.WhenAll(responseATask, responseBTask);

		var statusCodes = new[] { (await responseATask).StatusCode, (await responseBTask).StatusCode };

		// Domain writes already commit through one PostgreSQL transaction per compound command
		// regardless of which host issues it (plan §2.1) -- this is a regression guard, not a red
		// test: it is expected to pass today, proving the two-host fixture does not itself break
		// already-correct cross-host concurrency.
		statusCodes.Should().ContainSingle(code => code == HttpStatusCode.OK);
		statusCodes.Should().ContainSingle(code => code == HttpStatusCode.Conflict);
	}

	// ADR 0066 Stage 5: both rate-limit tests opt into the shared PostgreSQL counter store so the
	// two-host fixture proves the *global* limit, not each host's own independent in-process one.
	private static Dictionary<string, string?> LoginPermitLimitSetting(int permitLimit) =>
		new() { ["RateLimiting:LoginPermitLimit"] = permitLimit.ToString(CultureInfo.InvariantCulture), ["RateLimiting:Store"] = "PostgreSql" };

	private static Dictionary<string, string?> ApiPermitLimitSetting(int permitLimit) =>
		new() { ["RateLimiting:ApiPermitLimit"] = permitLimit.ToString(CultureInfo.InvariantCulture), ["RateLimiting:Store"] = "PostgreSql" };

	private static async Task<string> EstablishSessionCookieAsync(HttpClient client, string authCookie, string queryString)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/AwaitingProgress{queryString}");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		return WebTestHttp.ExtractCookiePair(WebTestHttp.FindSetCookie(response, "JobTrack.Filters") ?? throw new InvalidOperationException("No session cookie was set."));
	}

	private static async Task<string> GetAwaitingProgressBodyAsync(HttpClient client, string authCookie, string sessionCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Jobs/AwaitingProgress");
		request.Headers.Add("Cookie", $"{authCookie}; {sessionCookie}");
		var response = await client.SendAsync(request);
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		return await response.Content.ReadAsStringAsync();
	}

	private async Task<AppUserId> CreateEmployeeAsync(string userName, EmployeeRole role = EmployeeRole.Worker)
	{
		var result = await seedClient.Employees.CreateEmployeeAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			DisplayName = userName,
			IanaTimeZone = "Etc/UTC",
			UserName = userName,
			Password = KnownPassword,
			Role = role,
		});
		await ClearRequiresPasswordChangeAsync();

		return result.Id;
	}

	private async Task<JobNodeId> AddUnassignedLeafAsync(string description)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = rootId,
			Description = description,
			OwnerUserId = null,
			Priority = Priority.Medium,
		});

		return result.Id;
	}

	private async Task<JobNodeId> AddLeafWithWorkAsync(AppUserId? ownerId, string description)
	{
		var leaf = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = rootId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});
		var attached = await seedClient.Jobs.AttachLeafWorkAsync(
			new() { Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() }, JobNodeId = leaf.Id });

		return attached.JobNodeId;
	}

	private async Task ClearRequiresPasswordChangeAsync()
	{
		await using var connection = new NpgsqlConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "UPDATE identity_user SET requires_password_change = false;";
		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task DeploySchemaAsync()
	{
		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.PostgreSql));
		await using var connection = new NpgsqlConnection(database.ConnectionString);
		await connection.OpenAsync();
		var deployer = new SchemaDeployer(
			connection, new PostgreSqlSchemaVersionStore(), new PostgreSqlDeploymentLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
		await PostgreSqlTestInfrastructure.EnsureSecurityDefinerFunctionsAsync(connection, SchemaProvider.PostgreSql);
	}

	private string CreateTemporaryDirectory(string label)
	{
		var path = Path.Combine(Path.GetTempPath(), $"jobtrack-two-host-{label}-{Guid.NewGuid():N}");
		_ = Directory.CreateDirectory(path);
		temporaryDirectories.Add(path);

		return path;
	}

	private static async Task<(string CookieHeader, string Token)> GetLoginFormAsync(HttpClient client)
	{
		var response = await client.GetAsync("/Account/Login");
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in login page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in login page body.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}

	private static async Task<string> SignInAsync(HttpClient client, string userName)
	{
		var response = await PostLoginAsync(client, userName, KnownPassword);
		var authCookie = WebTestHttp.FindSetCookie(response, "Identity.Application") ??
						 throw new InvalidOperationException("Sign-in did not set the authentication cookie.");

		return WebTestHttp.ExtractCookiePair(authCookie);
	}

	private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string userName, string password)
	{
		var (antiforgeryCookie, token) = await GetLoginFormAsync(client);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = userName,
			["Input.Password"] = password,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private static async Task<HttpResponseMessage> PostTwoFactorCodeAsync(HttpClient client, string twoFactorUserIdCookie, string code)
	{
		var (antiforgeryCookie, token) = await GetFormAsync(client, "/Account/LoginTwoFactor", twoFactorUserIdCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/LoginTwoFactor");
		request.Headers.Add("Cookie", $"{twoFactorUserIdCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.Code"] = code,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private static async Task<(string Secret, string AuthCookie, string AntiforgeryCookie, string Token)> GetEnrolmentFormAsync(
		HttpClient client, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/ManageTwoFactor");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		var reissued = WebTestHttp.FindSetCookie(response, "Identity.Application");
		var refreshedAuthCookie = reissued is not null ? WebTestHttp.ExtractCookiePair(reissued) : authCookie;
		var antiforgeryCookie = WebTestHttp.ExtractCookiePair(
			WebTestHttp.FindSetCookie(response, "Antiforgery") ?? throw new InvalidOperationException("No antiforgery cookie in the enrolment page response."));
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } tokenMatch
			? tokenMatch.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in the enrolment page body.");
		var secret = AuthenticatorKeyPattern().Match(body) is { Success: true } keyMatch
			? keyMatch.Groups["key"].Value
			: throw new InvalidOperationException("No authenticator key in the enrolment page body.");

		return (secret, refreshedAuthCookie, antiforgeryCookie, token);
	}

	private static async Task<HttpResponseMessage> PostConfirmAsync(HttpClient client, string authCookie, string antiforgeryCookie, string token,
		string code)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/ManageTwoFactor?handler=Confirm");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Confirm.Code"] = code,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private static async Task<(string CookieHeader, string Token)> GetFormAsync(HttpClient client, string path, string? extraCookie = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		if (extraCookie is not null) {
			request.Headers.Add("Cookie", extraCookie);
		}

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException($"No antiforgery cookie in {path} response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException($"No antiforgery token in {path} body.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}

	private static async Task<(string CookieHeader, string Token)> GetAntiforgeryTokenAsync(HttpClient client, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery-token");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in token response.");
		var token = JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()
					?? throw new InvalidOperationException("No antiforgery token in token response.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}

	private static string GenerateTotpCode(string base32Secret, DateTimeOffset timestamp)
	{
		var key = Base32Decode(base32Secret);
		var counter = (long)(timestamp - DateTimeOffset.UnixEpoch).TotalSeconds / TotpStepSeconds;
		var counterBytes = BitConverter.GetBytes(counter);
		if (BitConverter.IsLittleEndian) {
			Array.Reverse(counterBytes);
		}

		// HMAC-SHA1 is RFC 6238's mandated TOTP algorithm, not a discretionary weak-crypto choice --
		// the same algorithm AuthenticatorTokenProvider<TUser> uses internally to verify the code.
#pragma warning disable CA5350
		using var hmac = new HMACSHA1(key);
#pragma warning restore CA5350
		var hash = hmac.ComputeHash(counterBytes);
		var offset = hash[^1] & 0x0F;
		var binaryCode =
			((hash[offset] & 0x7F) << 24) |
			((hash[offset + 1] & 0xFF) << 16) |
			((hash[offset + 2] & 0xFF) << 8) |
			(hash[offset + 3] & 0xFF);
		var truncated = binaryCode % (int)Math.Pow(10, TotpDigits);

		return truncated.ToString(CultureInfo.InvariantCulture).PadLeft(TotpDigits, '0');
	}

	private static byte[] Base32Decode(string base32)
	{
		var trimmed = base32.TrimEnd('=').ToUpperInvariant();
		var output = new List<byte>();
		var bitBuffer = 0;
		var bitCount = 0;

		foreach (var c in trimmed) {
			bitBuffer = (bitBuffer << 5) | Base32Alphabet.IndexOf(c, StringComparison.Ordinal);
			bitCount += 5;
			if (bitCount >= 8) {
				output.Add((byte)((bitBuffer >> (bitCount - 8)) & 0xFF));
				bitCount -= 8;
			}
		}

		return [.. output];
	}

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	[GeneratedRegex("Enter this key manually: <code>(?<key>[^<]+)</code>")]
	private static partial Regex AuthenticatorKeyPattern();

	private sealed class TestWebApplicationFactory(
		string connectionString,
		string dataProtectionKeyPath,
		IReadOnlyDictionary<string, string?>? extraSettings = null) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", "PostgreSql");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", connectionString);
			_ = builder.UseSetting("ConnectionStrings:JobTrackDomain", connectionString);
			_ = builder.UseSetting("ConnectionStrings:JobTrackPatManagement", connectionString);
			_ = builder.UseSetting("ConnectionStrings:JobTrackPatAuthentication", connectionString);
			_ = builder.UseSetting("DataProtection:KeyPath", dataProtectionKeyPath);

			if (extraSettings is not null) {
				foreach (var (key, value) in extraSettings) {
					_ = builder.UseSetting(key, value);
				}
			}
		}
	}
}
