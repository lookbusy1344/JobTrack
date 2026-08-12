namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Abstractions;
using AwesomeAssertions;
using Database;
using Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;
using TestSupport;
using Program = Program;

/// <summary>
///     ADR 0057 (§2.2/§2.3): direct-HTTP proof that a session cannot outlive its absolute ceiling
///     regardless of activity, and that a sensitive handler (PAT issuance, here as the representative
///     case already covered end-to-end in <see cref="PersonalAccessTokenManagementTests" />) refuses a
///     stale-but-authenticated session until <c>/Account/ConfirmAccess</c> re-proves the password.
/// </summary>
public sealed partial class StepUpAndAbsoluteSessionTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string AuthenticatorKeyProtectionPurpose = "JobTrack.Identity.AuthenticatorKey.v1";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const int LoginRateLimitPermitLimit = 2;
	private readonly MutableClock clock = new(Instant.FromUtc(2026, 8, 1, 9, 0, 0));

	private readonly SqliteDatabaseFixture database = new();
	private HttpClient client = null!;
	private TestWebApplicationFactory factory = null!;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

		factory = new(database.ConnectionString, clock);
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
	public async Task A_session_within_the_absolute_ceiling_stays_authenticated()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "ceiling.within");
		var authCookie = await SignInAsync("ceiling.within");

		clock.Advance(Duration.FromHours(7) + Duration.FromMinutes(59));

		var response = await GetPersonalAccessTokensPageAsync(authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task A_session_past_the_absolute_ceiling_is_rejected_even_though_it_was_never_idle()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "ceiling.past");
		var authCookie = await SignInAsync("ceiling.past");

		clock.Advance(Duration.FromHours(8) + Duration.FromMinutes(1));

		var response = await GetPersonalAccessTokensPageAsync(authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
	}

	[Fact]
	public async Task A_sensitive_action_is_redirected_to_confirm_access_once_recent_authentication_goes_stale()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "stepup.stale");
		var authCookie = await SignInAsync("stepup.stale");

		clock.Advance(Duration.FromMinutes(16));

		var response = await PostIssueAsync(authCookie, "laptop", 30);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/ConfirmAccess");
	}

	[Fact]
	public async Task Creating_an_employee_is_redirected_to_confirm_access_once_recent_authentication_goes_stale()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "stepup.create", EmployeeRole.Administrator);
		var authCookie = await SignInAsync("stepup.create");

		clock.Advance(Duration.FromMinutes(16));

		var response = await PostCreateEmployeeAsync(authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/ConfirmAccess");
	}

	[Fact]
	public async Task A_sensitive_action_succeeds_immediately_after_sign_in_while_recent_authentication_is_fresh()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "stepup.fresh");
		var authCookie = await SignInAsync("stepup.fresh");

		clock.Advance(Duration.FromMinutes(14));

		var response = await PostIssueAsync(authCookie, "laptop", 30);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().NotContain("/Account/ConfirmAccess");
	}

	[Fact]
	public async Task Confirming_access_with_the_correct_password_lets_the_sensitive_action_proceed()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "stepup.confirm");
		var authCookie = await SignInAsync("stepup.confirm");

		clock.Advance(Duration.FromMinutes(16));

		var confirmed = await ConfirmAccessAsync(authCookie, KnownPassword, "/Account/PersonalAccessTokens");
		confirmed.StatusCode.Should().Be(HttpStatusCode.Redirect);
		confirmed.Headers.Location!.OriginalString.Should().NotContain("/Account/ConfirmAccess");

		// The confirmation refreshed the session's own cookie (JobTrackSignInManager restamps
		// `recent` via RefreshSignInAsync) -- carry that new cookie forward rather than the
		// pre-confirmation one, exactly as a real browser would.
		var refreshedCookie = WebTestHttp.FindSetCookie(confirmed, "Identity.Application");
		authCookie = refreshedCookie is not null ? WebTestHttp.ExtractCookiePair(refreshedCookie) : authCookie;

		var response = await PostIssueAsync(authCookie, "laptop", 30);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().NotContain("/Account/ConfirmAccess");
	}

	[Fact]
	public async Task Confirming_access_with_the_wrong_password_does_not_refresh_recent_authentication()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "stepup.wrong");
		var authCookie = await SignInAsync("stepup.wrong");

		clock.Advance(Duration.FromMinutes(16));

		var confirmed = await ConfirmAccessAsync(authCookie, "not-the-password", "/Account/PersonalAccessTokens");
		confirmed.StatusCode.Should().Be(HttpStatusCode.OK);

		var response = await PostIssueAsync(authCookie, "laptop", 30);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/ConfirmAccess");
	}

	[Fact]
	public async Task Confirming_access_with_repeated_wrong_two_factor_codes_is_rate_limited()
	{
		var userId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "stepup.2fa");
		var authCookie = await SignInAsync("stepup.2fa");
		await EnableTwoFactorAsync(userId, "JBSWY3DPEHPK3PXP");

		var first = await ConfirmAccessAsync(authCookie, KnownPassword, "/Account/PersonalAccessTokens", "000000");
		var second = await ConfirmAccessAsync(authCookie, KnownPassword, "/Account/PersonalAccessTokens", "000000");
		var limited = await ConfirmAccessAsync(authCookie, KnownPassword, "/Account/PersonalAccessTokens", "000000");

		first.StatusCode.Should().Be(HttpStatusCode.OK);
		second.StatusCode.Should().Be(HttpStatusCode.OK);
		limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
	}

	private async Task<HttpResponseMessage> GetPersonalAccessTokensPageAsync(string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/PersonalAccessTokens");
		request.Headers.Add("Cookie", authCookie);
		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostIssueAsync(string authCookie, string label, int lifetimeDays)
	{
		var (antiforgeryCookie, token) = await GetAntiforgeryFormAsync(authCookie, "/Account/PersonalAccessTokens");

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/PersonalAccessTokens?handler=Issue");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Issue.Label"] = label,
			["Issue.LifetimeDays"] = lifetimeDays.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostCreateEmployeeAsync(string authCookie)
	{
		var (antiforgeryCookie, token) = await GetAntiforgeryFormAsync(authCookie, "/Admin/ManageEmployeeAccount");

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/ManageEmployeeAccount?handler=CreateEmployee");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["CreateEmployee.DisplayName"] = "New Employee",
			["CreateEmployee.IanaTimeZone"] = "Etc/UTC",
			["CreateEmployee.DefaultHourlyRate"] = "20.00",
			["CreateEmployee.UserName"] = "new.employee",
			["CreateEmployee.Password"] = KnownPassword,
			["CreateEmployee.Role"] = nameof(EmployeeRole.Worker),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> ConfirmAccessAsync(
		string authCookie,
		string password,
		string returnUrl,
		string? twoFactorCode = null)
	{
		var (antiforgeryCookie, token) =
			await GetAntiforgeryFormAsync(authCookie, $"/Account/ConfirmAccess?returnUrl={Uri.EscapeDataString(returnUrl)}");

		using var request = new HttpRequestMessage(HttpMethod.Post, $"/Account/ConfirmAccess?returnUrl={Uri.EscapeDataString(returnUrl)}");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.CurrentPassword"] = password,
			["Input.TwoFactorCode"] = twoFactorCode ?? string.Empty,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetAntiforgeryFormAsync(string? authCookie, string path)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		if (!string.IsNullOrEmpty(authCookie)) {
			request.Headers.Add("Cookie", authCookie);
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

	private async Task<string> SignInAsync(string userName)
	{
		var (antiforgeryCookie, token) = await GetAntiforgeryFormAsync(null, "/Account/Login");

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = userName,
			["Input.Password"] = KnownPassword,
			["__RequestVerificationToken"] = token,
		});

		var response = await client.SendAsync(request);
		var authCookie = WebTestHttp.FindSetCookie(response, "Identity.Application") ??
						 throw new InvalidOperationException("Sign-in did not set the authentication cookie.");

		return WebTestHttp.ExtractCookiePair(authCookie);
	}

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();





	private async Task EnableTwoFactorAsync(AppUserId appUserId, string base32Secret)
	{
		var dataProtectionProvider = factory.Services.GetRequiredService<IDataProtectionProvider>();
		var protector = dataProtectionProvider.CreateProtector(AuthenticatorKeyProtectionPurpose);
		var protectedKey = protector.Protect(Encoding.UTF8.GetBytes(base32Secret));

		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText =
			"UPDATE identity_user SET two_factor_enabled = 1, authenticator_key_protected = $key WHERE app_user_id = $appUserId;";
		_ = command.Parameters.AddWithValue("$key", protectedKey);
		_ = command.Parameters.AddWithValue("$appUserId", appUserId.Value);
		_ = await command.ExecuteNonQueryAsync();
	}

	/// <summary>Test-only <see cref="IClock" /> the factory substitutes for the app's <c>SystemClock.Instance</c> registration.</summary>
	private sealed class MutableClock(Instant now) : IClock
	{
		private Instant _now = now;

		public Instant GetCurrentInstant() => _now;

		public void Advance(Duration duration) => _now += duration;
	}

	private sealed class TestWebApplicationFactory(string identityConnectionString, IClock clock) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
			_ = builder.UseSetting("RateLimiting:LoginPermitLimit", LoginRateLimitPermitLimit.ToString(CultureInfo.InvariantCulture));
			_ = builder.ConfigureTestServices(services => {
				services.RemoveAll<IClock>();
				_ = services.AddSingleton(clock);
			});
		}
	}
}
