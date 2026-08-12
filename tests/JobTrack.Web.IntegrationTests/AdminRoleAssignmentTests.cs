namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
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
///     Direct-HTTP negative tests for the Administrator-only role-assignment page (plan §8.3),
///     covering threat-model rows 6 (authorization bypass, <c>TC-WEB-AUTHZ-001</c>) and 9 (mass
///     assignment, <c>TC-WEB-AUTHZ-004</c>). IDOR (row 7) and subtree-scope confusion (row 8) need
///     job/work endpoints from later §8.5 slices and are out of scope here.
/// </summary>
public sealed partial class AdminRoleAssignmentTests : IAsyncLifetime, IDisposable
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
	public async Task An_unauthenticated_get_request_is_redirected_to_sign_in()
	{
		var response = await client.GetAsync("/Admin/AssignRole");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
	}

	[Fact]
	public async Task An_unauthenticated_post_request_is_redirected_to_sign_in_without_reaching_the_handler()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "worker.unauth", EmployeeRole.Worker);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/AssignRole");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.TargetUserId"] = workerId.Value.ToString(CultureInfo.InvariantCulture),
			["Input.Role"] = nameof(EmployeeRole.RateManager),
			["Input.Revoke"] = "false",
		});

		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
	}

	[Fact]
	public async Task An_authenticated_non_administrator_is_denied()
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "worker.requester", EmployeeRole.Worker);
		var targetId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "worker.target", EmployeeRole.Worker);
		var authCookie = await SignInAsync("worker.requester");

		using var request = new HttpRequestMessage(HttpMethod.Get, "/Admin/AssignRole");
		request.Headers.Add("Cookie", authCookie);
		var getResponse = await client.SendAsync(request);

		getResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		getResponse.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
		_ = targetId;
	}

	[Fact]
	public async Task An_administrator_can_assign_a_role()
	{
		var adminId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "admin.assigner", EmployeeRole.Administrator);
		var targetId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "worker.assignee", EmployeeRole.Worker);
		var authCookie = await SignInAsync("admin.assigner");

		var response = await PostAssignRoleAsync(authCookie, targetId, EmployeeRole.RateManager, false);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await client.FollowRedirectAsync(response, authCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Rate Manager");
		_ = adminId;
	}

	[Fact]
	public async Task An_unexpected_extra_form_field_has_no_effect_beyond_the_allow_listed_fields()
	{
		var adminId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "admin.mass-assignment", EmployeeRole.Administrator);
		var targetId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "worker.mass-assignment", EmployeeRole.Worker);
		var authCookie = await SignInAsync("admin.mass-assignment");
		var (antiforgeryCookie, token) = await GetAssignRoleFormAsync(authCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/AssignRole");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.TargetUserId"] = targetId.Value.ToString(CultureInfo.InvariantCulture),
			["Input.Role"] = nameof(EmployeeRole.RateManager),
			["Input.Revoke"] = "false",
			["Input.IsEnabled"] = "false",
			["__RequestVerificationToken"] = token,
		});

		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var isStillEnabled = await IsEmployeeEnabledAsync(targetId);
		isStillEnabled.Should().BeTrue();
		_ = adminId;
	}

	private async Task<HttpResponseMessage> PostAssignRoleAsync(string authCookie, AppUserId targetId, EmployeeRole role, bool revoke)
	{
		var (antiforgeryCookie, token) = await GetAssignRoleFormAsync(authCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/AssignRole");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.TargetUserId"] = targetId.Value.ToString(CultureInfo.InvariantCulture),
			["Input.Role"] = role.ToString(),
			["Input.Revoke"] = revoke.ToString(),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetAssignRoleFormAsync(string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Admin/AssignRole");
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in AssignRole page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in AssignRole page body.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}

	private async Task<string> SignInAsync(string userName)
	{
		using var loginFormRequest = new HttpRequestMessage(HttpMethod.Get, "/Account/Login");
		var loginFormResponse = await client.SendAsync(loginFormRequest);
		var loginFormBody = await loginFormResponse.Content.ReadAsStringAsync();
		var loginAntiforgeryCookie = WebTestHttp.ExtractCookiePair(WebTestHttp.FindSetCookie(loginFormResponse, "Antiforgery") ??
													   throw new InvalidOperationException("No antiforgery cookie in login page response."));
		var loginToken = AntiforgeryTokenPattern().Match(loginFormBody) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in login page body.");

		using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		loginRequest.Headers.Add("Cookie", loginAntiforgeryCookie);
		loginRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = userName,
			["Input.Password"] = KnownPassword,
			["__RequestVerificationToken"] = loginToken,
		});

		var loginResponse = await client.SendAsync(loginRequest);
		var authCookie = WebTestHttp.FindSetCookie(loginResponse, "Identity.Application") ??
						 throw new InvalidOperationException("Sign-in did not set the authentication cookie.");

		return WebTestHttp.ExtractCookiePair(authCookie);
	}

	/// <summary>
	///     Follows a redirect response, carrying forward any cookie the redirect itself set (notably
	///     the TempData cookie a mutating handler's <c>SuccessMessage</c>/<c>ErrorMessage</c> rides in
	///     on) alongside the caller's own auth cookie.
	/// </summary>


	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();



	private async Task<bool> IsEmployeeEnabledAsync(AppUserId appUserId)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT is_enabled FROM identity_user WHERE app_user_id = $appUserId;";
		_ = command.Parameters.AddWithValue("$appUserId", appUserId.Value);

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) == 1;
	}



}
