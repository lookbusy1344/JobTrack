namespace JobTrack.Web.IntegrationTests;

using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;
using TestSupport;
using Program = Program;

/// <summary>
///     Passkey step-up on <c>/Account/ConfirmAccess</c> (ADR 0071 §8.3): the option handler emits
///     request options restricted to the signed-in user, and the confirm handler fails generically for a
///     bad or stateless assertion without refreshing recent authentication. These cover rendering,
///     user-scoped option generation, and the failure branches over real HTTP against SQLite. The
///     successful-assertion happy path is split by concern: origin preservation and recent-auth refresh
///     against a controllable clock live in <see cref="ConfirmAccessPasskeyStepUpOriginTests" />, and the
///     end-to-end ceremony (real assertion, no TOTP, return to caller) is Stage 7 browser evidence.
/// </summary>
public sealed partial class ConfirmAccessPasskeyStepUpTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";

	private readonly MutableClock clock = new(Instant.FromUtc(2026, 8, 1, 9, 0, 0));
	private readonly SqliteDatabaseFixture database = new();
	private HttpClient client = null!;
	private PasskeyStepUpWebApplicationFactory factory = null!;
	private static byte[] SeededCredentialId => [10, 20, 30, 40, 50, 60, 70, 80];

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

		factory = new(database.ConnectionString, clock, true);
		client = factory.CreateClient(new() {
			AllowAutoRedirect = false,
			HandleCookies = false,
		});
	}

	public async Task DisposeAsync()
	{
		Dispose();
		await database.DisposeAsync();
	}

	public void Dispose()
	{
		client.Dispose();
		factory.Dispose();
	}

	[Fact]
	public async Task Confirm_access_offers_passkey_step_up_when_the_user_has_a_passkey()
	{
		var userId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "stepup.haskey");
		await IdentityTestSupport.SeedSqlitePasskeyAsync(database.ConnectionString, userId, credentialId: SeededCredentialId);
		var authCookie = await SignInAsync("stepup.haskey");

		var body = await GetConfirmAccessBodyAsync(authCookie);

		body.Should().Contain("data-passkey-login");
		body.Should().Contain("Use a passkey");
		body.Should().Contain("js/passkeys");
	}

	[Fact]
	public async Task Confirm_access_omits_passkey_step_up_when_the_user_has_no_passkeys()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "stepup.nokey");
		var authCookie = await SignInAsync("stepup.nokey");

		var body = await GetConfirmAccessBodyAsync(authCookie);

		body.Should().NotContain("data-passkey-login");
		body.Should().NotContain("Use a passkey");
	}

	[Fact]
	public async Task Confirm_access_omits_passkey_step_up_when_the_feature_is_disabled()
	{
		using var disabledFactory = new PasskeyStepUpWebApplicationFactory(database.ConnectionString, clock);
		using var disabledClient = disabledFactory.CreateClient(new() {
			AllowAutoRedirect = false,
			HandleCookies = false,
		});
		var userId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "stepup.disabled");
		await IdentityTestSupport.SeedSqlitePasskeyAsync(database.ConnectionString, userId, credentialId: SeededCredentialId);
		var authCookie = await SignInWithAsync(disabledClient, "stepup.disabled");

		var body = await GetConfirmAccessBodyWithAsync(disabledClient, authCookie);

		body.Should().NotContain("data-passkey-login");
	}

	[Fact]
	public async Task Passkey_step_up_options_are_scoped_to_the_signed_in_user()
	{
		var userId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "stepup.scoped");
		await IdentityTestSupport.SeedSqlitePasskeyAsync(database.ConnectionString, userId, credentialId: SeededCredentialId);
		var authCookie = await SignInAsync("stepup.scoped");
		var (antiforgeryCookie, token) = await GetAntiforgeryFormAsync(authCookie, "/Account/ConfirmAccess");

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/ConfirmAccess?handler=PasskeyOptions");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Headers.Add("X-CSRF-TOKEN", token);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("challenge");
		// Unlike the username-less login request, a step-up request restricts allowCredentials to the
		// signed-in user's own credential -- so the seeded credential ID is present.
		body.Should().Contain(Base64Url.EncodeToString(SeededCredentialId));
	}

	[Fact]
	public async Task Passkey_step_up_options_are_refused_when_the_feature_is_disabled()
	{
		using var disabledFactory = new PasskeyStepUpWebApplicationFactory(database.ConnectionString, clock);
		using var disabledClient = disabledFactory.CreateClient(new() {
			AllowAutoRedirect = false,
			HandleCookies = false,
		});
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "stepup.optoff");
		var authCookie = await SignInWithAsync(disabledClient, "stepup.optoff");
		var (antiforgeryCookie, token) = await GetAntiforgeryFormWithAsync(disabledClient, authCookie, "/Account/ConfirmAccess");

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/ConfirmAccess?handler=PasskeyOptions");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Headers.Add("X-CSRF-TOKEN", token);
		var response = await disabledClient.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Passkey_step_up_with_an_invalid_assertion_fails_without_refreshing_recent_authentication()
	{
		var userId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "stepup.badassert");
		await IdentityTestSupport.SeedSqlitePasskeyAsync(database.ConnectionString, userId, credentialId: SeededCredentialId);
		var authCookie = await SignInAsync("stepup.badassert");

		// Go stale, so a sensitive action would demand step-up.
		clock.Advance(Duration.FromMinutes(16));

		var (antiforgeryCookie, token) = await GetAntiforgeryFormAsync(authCookie, "/Account/ConfirmAccess");
		using var optionsRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/ConfirmAccess?handler=PasskeyOptions");
		optionsRequest.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		optionsRequest.Headers.Add("X-CSRF-TOKEN", token);
		var optionsResponse = await client.SendAsync(optionsRequest);
		var cookies = string.Join("; ", new[] {
			authCookie, antiforgeryCookie,
		}.Concat(SetCookiePairs(optionsResponse)));

		using var request = new HttpRequestMessage(
			HttpMethod.Post, "/Account/ConfirmAccess?handler=PasskeyConfirm&returnUrl=%2FAccount%2FPersonalAccessTokens");
		request.Headers.Add("Cookie", cookies);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = new StringContent(
			"""{"id":"AAAA","rawId":"AAAA","type":"public-key","clientExtensionResults":{},"response":{"clientDataJSON":"AAAA","authenticatorData":"AAAA","signature":"AAAA"}}""",
			Encoding.UTF8,
			"application/json");
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("error");
		body.Should().NotContain("redirect");

		// Recent authentication is still stale: a sensitive action is redirected to ConfirmAccess.
		var sensitive = await PostIssueAsync(authCookie);
		sensitive.StatusCode.Should().Be(HttpStatusCode.Redirect);
		sensitive.Headers.Location!.OriginalString.Should().Contain("/Account/ConfirmAccess");
	}

	private static IEnumerable<string> SetCookiePairs(HttpResponseMessage response) =>
		response.Headers.TryGetValues("Set-Cookie", out var setCookies)
			? setCookies.Select(setCookie => setCookie.Split(';', 2)[0])
			: [];

	private Task<string> GetConfirmAccessBodyAsync(string authCookie) => GetConfirmAccessBodyWithAsync(client, authCookie);

	private static async Task<string> GetConfirmAccessBodyWithAsync(HttpClient httpClient, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/ConfirmAccess");
		request.Headers.Add("Cookie", authCookie);
		var response = await httpClient.SendAsync(request);
		return await response.Content.ReadAsStringAsync();
	}

	private async Task<HttpResponseMessage> PostIssueAsync(string authCookie)
	{
		var (antiforgeryCookie, token) = await GetAntiforgeryFormAsync(authCookie, "/Account/PersonalAccessTokens");

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/PersonalAccessTokens?handler=Issue");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Issue.Label"] = "laptop",
			["Issue.LifetimeDays"] = 30.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private Task<(string CookieHeader, string Token)> GetAntiforgeryFormAsync(string? authCookie, string path) =>
		GetAntiforgeryFormWithAsync(client, authCookie, path);

	private static async Task<(string CookieHeader, string Token)> GetAntiforgeryFormWithAsync(HttpClient httpClient, string? authCookie, string path)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		if (!string.IsNullOrEmpty(authCookie)) {
			request.Headers.Add("Cookie", authCookie);
		}

		var response = await httpClient.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException($"No antiforgery cookie in {path} response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException($"No antiforgery token in {path} body.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}

	private Task<string> SignInAsync(string userName) => SignInWithAsync(client, userName);

	private static async Task<string> SignInWithAsync(HttpClient httpClient, string userName)
	{
		var (antiforgeryCookie, token) = await GetAntiforgeryFormWithAsync(httpClient, null, "/Account/Login");

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = userName,
			["Input.Password"] = KnownPassword,
			["__RequestVerificationToken"] = token,
		});

		var response = await httpClient.SendAsync(request);
		var authCookie = WebTestHttp.FindSetCookie(response, "Identity.Application") ??
						 throw new InvalidOperationException("Sign-in did not set the authentication cookie.");

		return WebTestHttp.ExtractCookiePair(authCookie);
	}

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	private sealed class MutableClock(Instant now) : IClock
	{
		private Instant _now = now;

		public Instant GetCurrentInstant() => _now;

		public void Advance(Duration duration) => _now += duration;
	}

	private sealed class PasskeyStepUpWebApplicationFactory(string identityConnectionString, IClock clock, bool enablePasskeys = false)
		: WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
			_ = builder.UseSetting("Authentication:Passkeys:Enabled", enablePasskeys ? "true" : "false");
			if (enablePasskeys) {
				_ = builder.UseSetting("Authentication:Passkeys:ServerDomain", "localhost");
				_ = builder.UseSetting("Authentication:Passkeys:Origins:0", "http://localhost");
			}

			_ = builder.ConfigureTestServices(services => {
				services.RemoveAll<IClock>();
				_ = services.AddSingleton(clock);
			});
		}
	}
}
