namespace JobTrack.Web.IntegrationTests;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Abstractions;
using AwesomeAssertions;
using Identity;
using TestSupport;

/// <summary>
///     Username-less passkey sign-in on <c>/Account/Login</c> (ADR 0071 §8.2): the option handler emits
///     discoverable-credential request options without revealing which accounts exist, and the
///     assertion handler fails generically for a bad or stateless assertion without issuing a session.
///     The successful-assertion happy path (a real session with no TOTP step) needs a virtual
///     authenticator and is Stage 7; these cover the option generation and failure branches over real
///     HTTP against SQLite.
/// </summary>
public sealed partial class PasskeyLoginTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string FirstOrigin = "192.0.2.10";
	private const string SecondOrigin = "198.51.100.20";

	private readonly SqliteDatabaseFixture database = new();
	private HttpClient client = null!;
	private TestWebApplicationFactory factory = null!;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

		factory = new(database.ConnectionString, true);
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
	public async Task The_login_page_progressively_enhances_when_passkeys_are_enabled()
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/Login");
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("autocomplete=\"username webauthn\"");
		body.Should().Contain("data-passkey-login");
		body.Should().Contain("Sign in with a passkey");
		body.Should().Contain("js/passkeys");
	}

	[Fact]
	public async Task The_login_page_omits_the_passkey_enhancement_when_disabled()
	{
		using var disabledFactory = new TestWebApplicationFactory(database.ConnectionString);
		using var disabledClient = disabledFactory.CreateClient(new() {
			AllowAutoRedirect = false,
			HandleCookies = false,
		});

		using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/Login");
		var response = await disabledClient.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("autocomplete=\"username\"");
		body.Should().NotContain("webauthn");
		body.Should().NotContain("data-passkey-login");
		body.Should().NotContain("js/passkeys");
		// The password form is always present regardless of the enhancement.
		body.Should().Contain("autocomplete=\"current-password\"");
	}

	[Fact]
	public async Task Passkey_options_are_generated_username_less()
	{
		var (antiforgeryCookie, token) = await GetLoginAntiforgeryAsync();

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login?handler=PasskeyOptions");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("challenge");
		body.Should().Contain("localhost", "the configured RP ID is echoed in the request options");
		// A username-less request must not enumerate any account's credentials.
		body.Should().NotContain("\"allowCredentials\":[{");
	}

	[Fact]
	public async Task Exhausting_one_origins_passkey_options_budget_does_not_consume_another_origins_budget()
	{
		using var limitedFactory = new TestWebApplicationFactory(database.ConnectionString, true, new OnePermitBackstopRateLimiter());
		using var limitedClient = CreateClient(limitedFactory);
		var (antiforgeryCookie, token) = await GetLoginAntiforgeryWithAsync(limitedClient);

		var first = await PostPasskeyOptionsAsync(limitedClient, antiforgeryCookie, token, FirstOrigin);
		var exhausted = await PostPasskeyOptionsAsync(limitedClient, antiforgeryCookie, token, FirstOrigin);
		var unrelated = await PostPasskeyOptionsAsync(limitedClient, antiforgeryCookie, token, SecondOrigin);

		first.StatusCode.Should().Be(HttpStatusCode.OK);
		exhausted.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
		unrelated.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Passkey_options_and_assertions_have_separate_origin_budgets()
	{
		using var limitedFactory = new TestWebApplicationFactory(database.ConnectionString, true, new OnePermitBackstopRateLimiter());
		using var limitedClient = CreateClient(limitedFactory);
		var (antiforgeryCookie, token) = await GetLoginAntiforgeryWithAsync(limitedClient);

		var options = await PostPasskeyOptionsAsync(limitedClient, antiforgeryCookie, token, FirstOrigin);
		var assertion = await PostPasskeyAssertionAsync(limitedClient, antiforgeryCookie, token, FirstOrigin);

		options.StatusCode.Should().Be(HttpStatusCode.OK);
		assertion.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Exhausting_one_origins_passkey_assertion_budget_does_not_consume_another_origins_budget()
	{
		using var limitedFactory = new TestWebApplicationFactory(database.ConnectionString, true, new OnePermitBackstopRateLimiter());
		using var limitedClient = CreateClient(limitedFactory);
		var (antiforgeryCookie, token) = await GetLoginAntiforgeryWithAsync(limitedClient);

		var first = await PostPasskeyAssertionAsync(limitedClient, antiforgeryCookie, token, FirstOrigin);
		var exhausted = await PostPasskeyAssertionAsync(limitedClient, antiforgeryCookie, token, FirstOrigin);
		var unrelated = await PostPasskeyAssertionAsync(limitedClient, antiforgeryCookie, token, SecondOrigin);

		first.StatusCode.Should().Be(HttpStatusCode.OK);
		exhausted.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
		unrelated.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Passkey_options_are_refused_when_the_feature_is_disabled()
	{
		using var disabledFactory = new TestWebApplicationFactory(database.ConnectionString);
		using var disabledClient = disabledFactory.CreateClient(new() {
			AllowAutoRedirect = false,
			HandleCookies = false,
		});
		var (antiforgeryCookie, token) = await GetLoginAntiforgeryWithAsync(disabledClient);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login?handler=PasskeyOptions");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		var response = await disabledClient.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Passkey_sign_in_with_an_invalid_assertion_fails_without_issuing_a_session()
	{
		var (antiforgeryCookie, token) = await GetLoginAntiforgeryAsync();

		using var optionsRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/Login?handler=PasskeyOptions");
		optionsRequest.Headers.Add("Cookie", antiforgeryCookie);
		optionsRequest.Headers.Add("X-CSRF-TOKEN", token);
		var optionsResponse = await client.SendAsync(optionsRequest);

		// Carry the antiforgery cookie and the framework's assertion-state cookie into the assertion.
		var cookies = string.Join("; ", new[] {
			antiforgeryCookie,
		}.Concat(SetCookiePairs(optionsResponse)));

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login?handler=PasskeySignIn");
		request.Headers.Add("Cookie", cookies);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = new StringContent(
			"""{"id":"AAAA","rawId":"AAAA","type":"public-key","clientExtensionResults":{},"response":{"clientDataJSON":"AAAA","authenticatorData":"AAAA","signature":"AAAA"}}""",
			Encoding.UTF8,
			"application/json");
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().NotContain("redirect");
		WebTestHttp.FindSetCookie(response, "Identity.Application").Should().BeNull("a failed assertion issues no session");
	}

	[Fact]
	public async Task Passkey_sign_in_without_ceremony_state_does_not_issue_a_session()
	{
		var (antiforgeryCookie, token) = await GetLoginAntiforgeryAsync();

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login?handler=PasskeySignIn");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = new StringContent(
			"""{"id":"AAAA","rawId":"AAAA","type":"public-key","clientExtensionResults":{},"response":{"clientDataJSON":"AAAA","authenticatorData":"AAAA","signature":"AAAA"}}""",
			Encoding.UTF8,
			"application/json");
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().Contain("error");
		body.Should().NotContain("redirect");
		WebTestHttp.FindSetCookie(response, "Identity.Application").Should().BeNull();
	}

	[Fact]
	public async Task Passkey_sign_in_rejects_oversized_json_before_the_framework_parser()
	{
		var (antiforgeryCookie, token) = await GetLoginAntiforgeryAsync();
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login?handler=PasskeySignIn");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Content = new StringContent(
			new('x', PasskeyPolicy.MaximumCredentialJsonByteLength + 1),
			Encoding.UTF8,
			"application/json");

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("error");
		WebTestHttp.FindSetCookie(response, "Identity.Application").Should().BeNull();
	}

	private static IEnumerable<string> SetCookiePairs(HttpResponseMessage response) =>
		response.Headers.TryGetValues("Set-Cookie", out var setCookies)
			? setCookies.Select(setCookie => setCookie.Split(';', 2)[0])
			: [];

	private Task<(string CookieHeader, string Token)> GetLoginAntiforgeryAsync() => GetLoginAntiforgeryWithAsync(client);

	private static HttpClient CreateClient(TestWebApplicationFactory webApplicationFactory) =>
		webApplicationFactory.CreateClient(new() {
			AllowAutoRedirect = false,
			HandleCookies = false,
		});

	private static async Task<HttpResponseMessage> PostPasskeyOptionsAsync(
		HttpClient httpClient,
		string antiforgeryCookie,
		string token,
		string remoteAddress)
	{
		using var request = CreatePasskeyRequest(HttpMethod.Post, "/Account/Login?handler=PasskeyOptions", antiforgeryCookie, token, remoteAddress);
		return await httpClient.SendAsync(request);
	}

	private static async Task<HttpResponseMessage> PostPasskeyAssertionAsync(
		HttpClient httpClient,
		string antiforgeryCookie,
		string token,
		string remoteAddress)
	{
		using var request = CreatePasskeyRequest(HttpMethod.Post, "/Account/Login?handler=PasskeySignIn", antiforgeryCookie, token, remoteAddress);
		request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
		return await httpClient.SendAsync(request);
	}

	private static HttpRequestMessage CreatePasskeyRequest(
		HttpMethod method,
		string path,
		string antiforgeryCookie,
		string token,
		string remoteAddress)
	{
		var request = new HttpRequestMessage(method, path);
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Headers.Add("X-CSRF-TOKEN", token);
		request.Headers.Add("X-Forwarded-For", remoteAddress);
		return request;
	}

	private static async Task<(string CookieHeader, string Token)> GetLoginAntiforgeryWithAsync(HttpClient httpClient)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/Login");
		var response = await httpClient.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery")
								?? throw new InvalidOperationException("No antiforgery cookie on the login page.");
		var token = AntiforgeryTokenPattern().Match(body).Groups["token"].Value;

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	private sealed class OnePermitBackstopRateLimiter : ILoginAttemptRateLimiter
	{
		private readonly ConcurrentDictionary<string, byte> consumedBackstops = new(StringComparer.Ordinal);

		public ValueTask<RateLimitOutcome> TryAcquireAsync(
			string partitionKey,
			string backstopKey,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var expectedBackstopKey = partitionKey.Replace(":request:", ":origin:", StringComparison.Ordinal);
			if (expectedBackstopKey == partitionKey || backstopKey != expectedBackstopKey) {
				throw new InvalidOperationException("Passkey request and origin limiter keys must use distinct, paired namespaces.");
			}

			var outcome = consumedBackstops.TryAdd(backstopKey, 0)
				? RateLimitOutcome.Allowed
				: RateLimitOutcome.Denied;
			return ValueTask.FromResult(outcome);
		}
	}
}
