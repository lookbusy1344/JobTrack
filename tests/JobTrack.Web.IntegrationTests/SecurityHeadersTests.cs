namespace JobTrack.Web.IntegrationTests;

using System.Net;
using System.Text;
using Abstractions;
using AwesomeAssertions;
using Database;
using Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     Threat-model row 5 (XSS, <c>TC-WEB-AUTHN-007</c>): every response carries a restrictive
///     Content-Security-Policy plus the other headers plan §8.2 lists ("frame restrictions, MIME
///     sniffing protection, referrer policy"). Checked against an unauthenticated page
///     (<c>/Account/Login</c>) so the assertion holds regardless of authentication state.
/// </summary>
public sealed class SecurityHeadersTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";

	private readonly SqliteDatabaseFixture database = new();
	private HttpClient client = null!;
	private TestWebApplicationFactory factory = null!;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

		factory = new(database.ConnectionString, true);
		client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost"), HandleCookies = false });
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
	public async Task The_login_page_response_carries_a_restrictive_content_security_policy()
	{
		var response = await client.GetAsync("/Account/Login");

		response.Headers.TryGetValues("Content-Security-Policy", out var values).Should().BeTrue();
		var csp = values!.Single();
		csp.Should().Contain("default-src 'self'");
		csp.Should().Contain("object-src 'none'");
		csp.Should().Contain("frame-ancestors 'none'");
		csp.Should().NotContain("unsafe-inline");
		csp.Should().NotContain("unsafe-eval");
	}

	[Fact]
	public async Task The_login_page_response_carries_mime_sniffing_frame_and_referrer_protections()
	{
		var response = await client.GetAsync("/Account/Login");

		response.Headers.TryGetValues("X-Content-Type-Options", out var contentTypeValues).Should().BeTrue();
		contentTypeValues!.Single().Should().Be("nosniff");

		response.Headers.TryGetValues("X-Frame-Options", out var frameValues).Should().BeTrue();
		frameValues!.Single().Should().Be("DENY");

		response.Headers.TryGetValues("Referrer-Policy", out var referrerValues).Should().BeTrue();
		referrerValues!.Single().Should().Be("no-referrer");
	}

	[Fact]
	public async Task The_login_page_response_carries_no_store_cache_control_so_the_browser_cannot_replay_it_after_logout()
	{
		var response = await client.GetAsync("/Account/Login");

		response.Headers.CacheControl.Should().NotBeNull();
		response.Headers.CacheControl!.NoStore.Should().BeTrue();
		response.Headers.CacheControl.NoCache.Should().BeTrue();
		response.Headers.Pragma.Should().ContainSingle(value => value.Name == "no-cache");
	}

	[Fact]
	public async Task Every_cookie_issued_by_the_login_page_is_restricted_to_secure_transport()
	{
		var response = await client.GetAsync("/Account/Login");

		response.Headers.TryGetValues("Set-Cookie", out var values).Should().BeTrue();
		values.Should().NotBeEmpty();
		values.Should().AllSatisfy(value => value.Should().Contain("; secure", Exactly.Once()));
	}

	[Fact]
	public async Task Single_instance_demo_preserves_same_as_request_cookie_transport_policy()
	{
		using var demoFactory = new TestWebApplicationFactory(database.ConnectionString, false);
		using var demoClient = demoFactory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });

		var response = await demoClient.GetAsync("/Account/Login");

		response.Headers.TryGetValues("Set-Cookie", out var values).Should().BeTrue();
		values.Should().NotBeEmpty();
		values.Should().AllSatisfy(value => value.Should().NotContain("; secure"));
	}

	/// <summary>
	///     The login page's own no-store header must not be an accident of the antiforgery cookie it
	///     happens to issue on every request -- a routine authenticated navigation after sign-in
	///     (<c>/Account/PersonalAccessTokens</c>) needs the same protection so the browser can't replay
	///     it once the employee has logged out or lost the role that granted them access.
	/// </summary>
	[Fact]
	public async Task An_authenticated_navigation_after_sign_in_carries_no_store_cache_control()
	{
		await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "cache.worker", EmployeeRole.Worker);
		var authCookie = await client.SignInAsync("cache.worker");

		using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/PersonalAccessTokens");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);

		response.Headers.CacheControl.Should().NotBeNull();
		response.Headers.CacheControl!.NoStore.Should().BeTrue();
	}

	/// <summary>
	///     An unauthenticated request to a protected page is redirected to sign-in before any page
	///     content renders, so it issues no cookie of any kind -- unlike every other page exercised
	///     above, it gets no incidental no-store side effect from the antiforgery system or from the
	///     security-stamp revalidation on every authenticated request (<see cref="Web.Program" />'s
	///     <c>SecurityStampValidatorOptions.ValidationInterval = TimeSpan.Zero</c>). This is the one
	///     case that depends entirely on a deliberate, page-independent cache policy.
	/// </summary>
	[Fact]
	public async Task An_unauthenticated_redirect_that_issues_no_cookie_still_carries_no_store_cache_control()
	{
		var response = await client.GetAsync("/Account/PersonalAccessTokens");

		response.Headers.Contains("Set-Cookie").Should().BeFalse();
		response.Headers.CacheControl.Should().NotBeNull();
		response.Headers.CacheControl!.NoStore.Should().BeTrue();
	}

	[Fact]
	public async Task A_fingerprinted_static_asset_keeps_its_own_long_lived_cache_control_instead_of_no_store()
	{
		var response = await client.GetAsync("/favicon.ico");

		response.Headers.CacheControl.Should().NotBeNull();
		response.Headers.CacheControl!.NoStore.Should().BeFalse();
	}

	[Fact]
	public async Task Cross_origin_requests_receive_no_access_control_allow_origin_header()
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/Login");
		request.Headers.Add("Origin", "https://evil.example");

		var response = await client.SendAsync(request);

		response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
	}

	[Fact]
	public async Task A_request_body_over_the_size_limit_is_rejected_before_reaching_page_handling()
	{
		const int oversizedBodyBytes = 128 * 1024;
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Content = new StringContent(new('a', oversizedBodyBytes), Encoding.UTF8, "application/x-www-form-urlencoded");

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		body.Should().Contain("request-too-large");
	}







	private async Task<(string CookieHeader, string Token)> GetLoginFormAsync()
	{
		var response = await client.GetAsync("/Account/Login");
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in login page response.");
		var token = AntiforgeryTokenValue(body) ?? throw new InvalidOperationException("No antiforgery token in login page body.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}

	private static string? AntiforgeryTokenValue(string body)
	{
		const string marker = "name=\"__RequestVerificationToken\"";
		var markerIndex = body.IndexOf(marker, StringComparison.Ordinal);
		if (markerIndex < 0) {
			return null;
		}

		const string valueMarker = "value=\"";
		var valueIndex = body.IndexOf(valueMarker, markerIndex, StringComparison.Ordinal);
		if (valueIndex < 0) {
			return null;
		}

		var start = valueIndex + valueMarker.Length;
		var end = body.IndexOf('"', start);
		return end < 0 ? null : body[start..end];
	}

	private sealed class TestWebApplicationFactory(string identityConnectionString, bool requireSecureCookies)
		: WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
			_ = builder.UseSetting("Security:RequireSecureCookies", requireSecureCookies.ToString());
		}
	}
}
