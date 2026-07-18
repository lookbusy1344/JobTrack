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
		await DeploySchemaAsync();

		factory = new(database.ConnectionString);
		client = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
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

	/// <summary>
	///     The login page's own no-store header must not be an accident of the antiforgery cookie it
	///     happens to issue on every request -- a routine authenticated navigation after sign-in
	///     (<c>/Account/PersonalAccessTokens</c>) needs the same protection so the browser can't replay
	///     it once the employee has logged out or lost the role that granted them access.
	/// </summary>
	[Fact]
	public async Task An_authenticated_navigation_after_sign_in_carries_no_store_cache_control()
	{
		await SeedEmployeeAsync("cache.worker", EmployeeRole.Worker);
		var authCookie = await SignInAsync("cache.worker");

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

	private async Task DeploySchemaAsync()
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using (var pragma = connection.CreateCommand()) {
			pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
			_ = await pragma.ExecuteNonQueryAsync();
		}

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.Sqlite));
		var deployer = new SchemaDeployer(connection, new SqliteSchemaVersionStore(), new SqliteDeploymentLockStrategy(), ApplicationVersion,
			AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}

	private async Task<AppUserId> SeedEmployeeAsync(string userName, EmployeeRole role)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var insertAppUser = connection.CreateCommand();
		insertAppUser.CommandText =
			"INSERT INTO app_user (display_name, iana_time_zone) VALUES ($displayName, 'UTC'); SELECT last_insert_rowid();";
		_ = insertAppUser.Parameters.AddWithValue("$displayName", userName);
		var appUserId = (long)(await insertAppUser.ExecuteScalarAsync())!;

		var placeholderUser = new JobTrackIdentityUser {
			AppUserId = new(appUserId),
			UserName = userName,
			NormalizedUserName = userName.ToUpperInvariant(),
			PasswordHash = string.Empty,
			SecurityStamp = Guid.NewGuid().ToString(),
			ConcurrencyStamp = Guid.NewGuid().ToString(),
		};
		var passwordHash = new PasswordHasher<JobTrackIdentityUser>().HashPassword(placeholderUser, KnownPassword);

		await using var insertIdentityUser = connection.CreateCommand();
		insertIdentityUser.CommandText = """
										 INSERT INTO identity_user
										 	(app_user_id, user_name, normalized_user_name, password_hash, security_stamp,
										 	 concurrency_stamp, requires_password_change, is_enabled, lockout_enabled, access_failed_count)
										 VALUES
										 	($appUserId, $userName, $normalizedUserName, $passwordHash, $securityStamp,
										 	 $concurrencyStamp, 0, 1, 1, 0);
										 """;
		_ = insertIdentityUser.Parameters.AddWithValue("$appUserId", appUserId);
		_ = insertIdentityUser.Parameters.AddWithValue("$userName", userName);
		_ = insertIdentityUser.Parameters.AddWithValue("$normalizedUserName", userName.ToUpperInvariant());
		_ = insertIdentityUser.Parameters.AddWithValue("$passwordHash", passwordHash);
		_ = insertIdentityUser.Parameters.AddWithValue("$securityStamp", placeholderUser.SecurityStamp);
		_ = insertIdentityUser.Parameters.AddWithValue("$concurrencyStamp", placeholderUser.ConcurrencyStamp);
		_ = await insertIdentityUser.ExecuteNonQueryAsync();

		await using var insertRole = connection.CreateCommand();
		insertRole.CommandText =
			"INSERT INTO identity_user_role (identity_user_id, identity_role_id) SELECT id, $roleId FROM identity_user WHERE app_user_id = $appUserId;";
		_ = insertRole.Parameters.AddWithValue("$appUserId", appUserId);
		_ = insertRole.Parameters.AddWithValue("$roleId", (short)role);
		_ = await insertRole.ExecuteNonQueryAsync();

		return new(appUserId);
	}

	private async Task<string> SignInAsync(string userName)
	{
		var (antiforgeryCookie, token) = await GetLoginFormAsync();

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = userName,
			["Input.Password"] = KnownPassword,
			["__RequestVerificationToken"] = token,
		});

		var response = await client.SendAsync(request);
		var authCookie = FindSetCookie(response, "Identity.Application") ??
						 throw new InvalidOperationException("Sign-in did not set the authentication cookie.");

		return ExtractCookiePair(authCookie);
	}

	private async Task<(string CookieHeader, string Token)> GetLoginFormAsync()
	{
		var response = await client.GetAsync("/Account/Login");
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in login page response.");
		var token = AntiforgeryTokenValue(body) ?? throw new InvalidOperationException("No antiforgery token in login page body.");

		return (ExtractCookiePair(antiforgeryCookie), token);
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

	private static string? FindSetCookie(HttpResponseMessage response, string nameContains) =>
		response.Headers.TryGetValues("Set-Cookie", out var values)
			? values.FirstOrDefault(value => value.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
			: null;

	private static string ExtractCookiePair(string setCookieHeader) => setCookieHeader.Split(';')[0];

	private sealed class TestWebApplicationFactory(string identityConnectionString) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
		}
	}
}
