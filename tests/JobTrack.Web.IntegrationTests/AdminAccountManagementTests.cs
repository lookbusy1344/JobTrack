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
///     Direct-HTTP tests for the Administrator-only account-management page (plan §8.3), closing
///     threat-model row 3 (session theft: disablement/reset must revoke the security stamp and kill a
///     live session, not just block future sign-ins) and covering row 6 (authorization bypass,
///     <c>TC-WEB-AUTHZ-001</c>) for both handlers.
/// </summary>
public sealed partial class AdminAccountManagementTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string NewPassword = "New-Horse-Battery-99!";

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
	public async Task An_administrator_can_create_a_new_employee()
	{
		_ = await SeedEmployeeAsync("admin.create", EmployeeRole.Administrator);
		var adminAuthCookie = await SignInAsync("admin.create");

		var response = await PostCreateEmployeeAsync(
			adminAuthCookie,
			"Grace Hopper",
			"Etc/UTC",
			20m,
			"grace.hopper.new",
			"Correct-Horse-Battery-42!",
			EmployeeRole.Worker);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, adminAuthCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("created");
		body.Should().Contain("grace.hopper.new");
	}

	[Fact]
	public async Task Creating_an_employee_without_posting_a_role_uses_the_page_default()
	{
		_ = await SeedEmployeeAsync("admin.create-default-role", EmployeeRole.Administrator);
		var adminAuthCookie = await SignInAsync("admin.create-default-role");

		var response = await PostCreateEmployeeWithoutRoleAsync(
			adminAuthCookie, "Default Role", "Etc/UTC", 20m, "default.role.new", "Correct-Horse-Battery-42!");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, adminAuthCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("created");
		body.Should().Contain("Worker");
		body.Should().NotContain("None");
	}

	[Fact]
	public async Task A_newly_created_employee_can_sign_in_and_is_forced_to_change_password()
	{
		_ = await SeedEmployeeAsync("admin.create-then-login", EmployeeRole.Administrator);
		var adminAuthCookie = await SignInAsync("admin.create-then-login");
		const string initialPassword = "Initial-Horse-Battery-11!";
		_ = await PostCreateEmployeeAsync(adminAuthCookie, "Ada Lovelace", "Etc/UTC", 20m, "ada.lovelace.new", initialPassword, EmployeeRole.Worker);

		var response = await PostLoginAsync("ada.lovelace.new", initialPassword);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/ChangePassword");
	}

	[Fact]
	public async Task An_administrator_can_provision_a_second_administrator()
	{
		_ = await SeedEmployeeAsync("admin.create-admin", EmployeeRole.Administrator);
		var adminAuthCookie = await SignInAsync("admin.create-admin");

		var response = await PostCreateEmployeeAsync(
			adminAuthCookie, "Second Admin", "Etc/UTC", 20m, "second.admin.new", "Correct-Horse-Battery-42!", EmployeeRole.Administrator);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, adminAuthCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("Administrator");
	}

	[Fact]
	public async Task Creating_an_employee_with_a_taken_username_shows_an_error()
	{
		_ = await SeedEmployeeAsync("admin.create-dup", EmployeeRole.Administrator);
		var adminAuthCookie = await SignInAsync("admin.create-dup");
		_ = await PostCreateEmployeeAsync(adminAuthCookie, "First", "Etc/UTC", 20m, "duplicate.username", "Correct-Horse-Battery-42!",
			EmployeeRole.Worker);

		var response = await PostCreateEmployeeAsync(adminAuthCookie, "Second", "Etc/UTC", 20m, "duplicate.username", "Correct-Horse-Battery-42!",
			EmployeeRole.Worker);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, adminAuthCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("already taken");
	}

	[Fact]
	public async Task A_non_administrator_cannot_create_an_employee()
	{
		var workerId = await SeedEmployeeAsync("worker.create-denied", EmployeeRole.Worker);
		var workerAuthCookie = await SignInAsync("worker.create-denied");

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/ManageEmployeeAccount?handler=CreateEmployee");
		request.Headers.Add("Cookie", workerAuthCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["CreateEmployee.DisplayName"] = "Someone",
			["CreateEmployee.IanaTimeZone"] = "Etc/UTC",
			["CreateEmployee.UserName"] = "someone.new",
			["CreateEmployee.Password"] = "Correct-Horse-Battery-42!",
			["CreateEmployee.Role"] = nameof(EmployeeRole.Worker),
		});
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
		_ = workerId;
	}

	[Fact]
	public async Task Disabling_an_employee_with_a_live_session_ends_that_session_on_its_next_request()
	{
		var adminId = await SeedEmployeeAsync("admin.disable-live", EmployeeRole.Administrator);
		var workerId = await SeedEmployeeAsync("worker.disable-live", EmployeeRole.Worker);
		var workerAuthCookie = await SignInAsync("worker.disable-live");
		var adminAuthCookie = await SignInAsync("admin.disable-live");

		using var beforeRequest = new HttpRequestMessage(HttpMethod.Get, "/Account/PersonalAccessTokens");
		beforeRequest.Headers.Add("Cookie", workerAuthCookie);
		var beforeResponse = await client.SendAsync(beforeRequest);
		beforeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

		var setEnabledResponse = await PostSetEnabledAsync(adminAuthCookie, workerId, false);
		setEnabledResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		(await IsEnabledAsync(workerId)).Should().BeFalse();

		using var afterRequest = new HttpRequestMessage(HttpMethod.Get, "/Account/PersonalAccessTokens");
		afterRequest.Headers.Add("Cookie", workerAuthCookie);
		var afterResponse = await client.SendAsync(afterRequest);

		afterResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		afterResponse.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
		_ = adminId;
	}

	[Fact]
	public async Task A_disabled_employee_cannot_sign_in_again()
	{
		var adminId = await SeedEmployeeAsync("admin.disable-then-login", EmployeeRole.Administrator);
		var workerId = await SeedEmployeeAsync("worker.disable-then-login", EmployeeRole.Worker);
		var adminAuthCookie = await SignInAsync("admin.disable-then-login");
		_ = await PostSetEnabledAsync(adminAuthCookie, workerId, false);

		var (antiforgeryCookie, token) = await GetLoginFormAsync();
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = "worker.disable-then-login",
			["Input.Password"] = KnownPassword,
			["__RequestVerificationToken"] = token,
		});
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		_ = adminId;
	}

	[Fact]
	public async Task An_administrator_can_reset_a_password_forcing_a_change_at_next_sign_in()
	{
		var adminId = await SeedEmployeeAsync("admin.reset", EmployeeRole.Administrator);
		var workerId = await SeedEmployeeAsync("worker.reset", EmployeeRole.Worker);
		var adminAuthCookie = await SignInAsync("admin.reset");

		var resetResponse = await PostResetPasswordAsync(adminAuthCookie, workerId, NewPassword);
		resetResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

		var oldPasswordResponse = await PostLoginAsync("worker.reset", KnownPassword);
		oldPasswordResponse.StatusCode.Should().Be(HttpStatusCode.OK);

		var newPasswordResponse = await PostLoginAsync("worker.reset", NewPassword);
		newPasswordResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		newPasswordResponse.Headers.Location!.OriginalString.Should().Contain("/Account/ChangePassword");
		_ = adminId;
	}

	/// <summary>
	///     TC-WEB-AUTHN-005 (threat model row 3): an administrator resetting a password revokes the
	///     employee's existing session, the same as disablement, since <c>UserManager.ResetPasswordAsync</c>
	///     rotates the security stamp the live cookie is validated against.
	/// </summary>
	[Fact]
	public async Task An_administrator_resetting_a_password_ends_the_employees_live_session_on_its_next_request()
	{
		var adminId = await SeedEmployeeAsync("admin.reset-live", EmployeeRole.Administrator);
		var workerId = await SeedEmployeeAsync("worker.reset-live", EmployeeRole.Worker);
		var workerAuthCookie = await SignInAsync("worker.reset-live");
		var adminAuthCookie = await SignInAsync("admin.reset-live");

		using var beforeRequest = new HttpRequestMessage(HttpMethod.Get, "/Account/PersonalAccessTokens");
		beforeRequest.Headers.Add("Cookie", workerAuthCookie);
		var beforeResponse = await client.SendAsync(beforeRequest);
		beforeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

		var resetResponse = await PostResetPasswordAsync(adminAuthCookie, workerId, NewPassword);
		resetResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

		using var afterRequest = new HttpRequestMessage(HttpMethod.Get, "/Account/PersonalAccessTokens");
		afterRequest.Headers.Add("Cookie", workerAuthCookie);
		var afterResponse = await client.SendAsync(afterRequest);

		afterResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		afterResponse.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
		_ = adminId;
	}

	[Fact]
	public async Task A_non_administrator_cannot_disable_an_account()
	{
		var workerId = await SeedEmployeeAsync("worker.set-enabled-denied", EmployeeRole.Worker);
		var otherWorkerId = await SeedEmployeeAsync("worker.set-enabled-target", EmployeeRole.Worker);
		var workerAuthCookie = await SignInAsync("worker.set-enabled-denied");

		// [Authorize(Policy = Administrator)] denies GET too, so there is no page render to pull a
		// real antiforgery token from -- the denial happens in authorization middleware, before
		// the page (and any token check) ever runs.
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/ManageEmployeeAccount?handler=SetEnabled");
		request.Headers.Add("Cookie", workerAuthCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["SetEnabled.TargetUserId"] = otherWorkerId.Value.ToString(CultureInfo.InvariantCulture),
			["SetEnabled.Enabled"] = "false",
		});
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
		_ = workerId;
	}

	[Fact]
	public async Task An_administrator_can_set_an_employees_default_hourly_rate()
	{
		_ = await SeedEmployeeAsync("admin.default-rate", EmployeeRole.Administrator);
		var workerId = await SeedEmployeeAsync("worker.default-rate", EmployeeRole.Worker);
		var adminAuthCookie = await SignInAsync("admin.default-rate");

		var response = await PostSetDefaultHourlyRateAsync(adminAuthCookie, workerId, 30m);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, adminAuthCookie);
		var body = await reloaded.Content.ReadAsStringAsync();
		body.Should().Contain("default hourly rate");
		(await GetDefaultHourlyRateAsync(workerId)).Should().Be(30m);
	}

	[Fact]
	public async Task A_non_administrator_cannot_set_an_employees_default_hourly_rate()
	{
		var workerId = await SeedEmployeeAsync("worker.default-rate-denied", EmployeeRole.Worker);
		var otherWorkerId = await SeedEmployeeAsync("worker.default-rate-target", EmployeeRole.Worker);
		var workerAuthCookie = await SignInAsync("worker.default-rate-denied");

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/ManageEmployeeAccount?handler=SetDefaultHourlyRate");
		request.Headers.Add("Cookie", workerAuthCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["SetDefaultHourlyRate.TargetUserId"] = otherWorkerId.Value.ToString(CultureInfo.InvariantCulture),
			["SetDefaultHourlyRate.DefaultHourlyRate"] = "30",
		});
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
		_ = workerId;
	}

	[Fact]
	public async Task A_non_administrator_cannot_reset_a_password()
	{
		var workerId = await SeedEmployeeAsync("worker.reset-denied", EmployeeRole.Worker);
		var otherWorkerId = await SeedEmployeeAsync("worker.reset-target", EmployeeRole.Worker);
		var workerAuthCookie = await SignInAsync("worker.reset-denied");

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/ManageEmployeeAccount?handler=ResetPassword");
		request.Headers.Add("Cookie", workerAuthCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["ResetPassword.TargetUserId"] = otherWorkerId.Value.ToString(CultureInfo.InvariantCulture),
			["ResetPassword.NewPassword"] = NewPassword,
		});
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
		_ = workerId;
	}

	[Fact]
	public async Task An_administrator_can_reset_two_factor()
	{
		var adminId = await SeedEmployeeAsync("admin.reset-2fa", EmployeeRole.Administrator);
		var workerId = await SeedEmployeeAsync("worker.reset-2fa", EmployeeRole.Worker);
		await SeedTwoFactorEnabledAsync(workerId);
		var adminAuthCookie = await SignInAsync("admin.reset-2fa");

		var resetResponse = await PostResetTwoFactorAsync(adminAuthCookie, workerId);

		resetResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var (enabled, _) = await GetTwoFactorStateAsync(workerId);
		enabled.Should().BeFalse();
		_ = adminId;
	}

	/// <summary>
	///     ADR 0037, same shape as <see cref="An_administrator_resetting_a_password_ends_the_employees_live_session_on_its_next_request" />:
	///     the reset rotates the security stamp the live cookie is validated against.
	/// </summary>
	[Fact]
	public async Task An_administrator_resetting_two_factor_ends_the_employees_live_session_on_its_next_request()
	{
		var adminId = await SeedEmployeeAsync("admin.reset-2fa-live", EmployeeRole.Administrator);
		var workerId = await SeedEmployeeAsync("worker.reset-2fa-live", EmployeeRole.Worker);
		var workerAuthCookie = await SignInAsync("worker.reset-2fa-live");
		await SeedTwoFactorEnabledAsync(workerId);
		var adminAuthCookie = await SignInAsync("admin.reset-2fa-live");

		using var beforeRequest = new HttpRequestMessage(HttpMethod.Get, "/Account/PersonalAccessTokens");
		beforeRequest.Headers.Add("Cookie", workerAuthCookie);
		var beforeResponse = await client.SendAsync(beforeRequest);
		beforeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

		var resetResponse = await PostResetTwoFactorAsync(adminAuthCookie, workerId);
		resetResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

		using var afterRequest = new HttpRequestMessage(HttpMethod.Get, "/Account/PersonalAccessTokens");
		afterRequest.Headers.Add("Cookie", workerAuthCookie);
		var afterResponse = await client.SendAsync(afterRequest);

		afterResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		afterResponse.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
		_ = adminId;
	}

	[Fact]
	public async Task A_non_administrator_cannot_reset_two_factor()
	{
		var workerId = await SeedEmployeeAsync("worker.reset-2fa-denied", EmployeeRole.Worker);
		var otherWorkerId = await SeedEmployeeAsync("worker.reset-2fa-target", EmployeeRole.Worker);
		var workerAuthCookie = await SignInAsync("worker.reset-2fa-denied");

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/ManageEmployeeAccount?handler=ResetTwoFactor");
		request.Headers.Add("Cookie", workerAuthCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["ResetTwoFactor.TargetUserId"] = otherWorkerId.Value.ToString(CultureInfo.InvariantCulture),
		});
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
		_ = workerId;
	}

	private async Task<HttpResponseMessage> PostCreateEmployeeAsync(
		string authCookie,
		string displayName,
		string ianaTimeZone,
		decimal defaultHourlyRate,
		string userName,
		string password,
		EmployeeRole role)
	{
		var (antiforgeryCookie, token) = await GetManageAccountFormAsync(authCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/ManageEmployeeAccount?handler=CreateEmployee");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["CreateEmployee.DisplayName"] = displayName,
			["CreateEmployee.IanaTimeZone"] = ianaTimeZone,
			["CreateEmployee.DefaultHourlyRate"] = defaultHourlyRate.ToString(CultureInfo.InvariantCulture),
			["CreateEmployee.UserName"] = userName,
			["CreateEmployee.Password"] = password,
			["CreateEmployee.Role"] = role.ToString(),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostCreateEmployeeWithoutRoleAsync(
		string authCookie, string displayName, string ianaTimeZone, decimal defaultHourlyRate, string userName, string password)
	{
		var (antiforgeryCookie, token) = await GetManageAccountFormAsync(authCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/ManageEmployeeAccount?handler=CreateEmployee");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["CreateEmployee.DisplayName"] = displayName,
			["CreateEmployee.IanaTimeZone"] = ianaTimeZone,
			["CreateEmployee.DefaultHourlyRate"] = defaultHourlyRate.ToString(CultureInfo.InvariantCulture),
			["CreateEmployee.UserName"] = userName,
			["CreateEmployee.Password"] = password,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostSetEnabledAsync(string authCookie, AppUserId targetId, bool enabled)
	{
		var (antiforgeryCookie, token) = await GetManageAccountFormAsync(authCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/ManageEmployeeAccount?handler=SetEnabled");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["SetEnabled.TargetUserId"] = targetId.Value.ToString(CultureInfo.InvariantCulture),
			["SetEnabled.Enabled"] = enabled.ToString(),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostSetDefaultHourlyRateAsync(string authCookie, AppUserId targetId, decimal defaultHourlyRate)
	{
		var (antiforgeryCookie, token) = await GetManageAccountFormAsync(authCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/ManageEmployeeAccount?handler=SetDefaultHourlyRate");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["SetDefaultHourlyRate.TargetUserId"] = targetId.Value.ToString(CultureInfo.InvariantCulture),
			["SetDefaultHourlyRate.DefaultHourlyRate"] = defaultHourlyRate.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostResetPasswordAsync(string authCookie, AppUserId targetId, string newPassword)
	{
		var (antiforgeryCookie, token) = await GetManageAccountFormAsync(authCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/ManageEmployeeAccount?handler=ResetPassword");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["ResetPassword.TargetUserId"] = targetId.Value.ToString(CultureInfo.InvariantCulture),
			["ResetPassword.NewPassword"] = newPassword,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostResetTwoFactorAsync(string authCookie, AppUserId targetId)
	{
		var (antiforgeryCookie, token) = await GetManageAccountFormAsync(authCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/ManageEmployeeAccount?handler=ResetTwoFactor");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["ResetTwoFactor.TargetUserId"] = targetId.Value.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task SeedTwoFactorEnabledAsync(AppUserId appUserId)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText =
			"UPDATE identity_user SET two_factor_enabled = 1, authenticator_key_protected = $key WHERE app_user_id = $appUserId;";
		_ = command.Parameters.AddWithValue("$key", new byte[] { 1, 2, 3 });
		_ = command.Parameters.AddWithValue("$appUserId", appUserId.Value);
		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task<(bool Enabled, byte[]? KeyProtected)> GetTwoFactorStateAsync(AppUserId appUserId)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT two_factor_enabled, authenticator_key_protected FROM identity_user WHERE app_user_id = $appUserId;";
		_ = command.Parameters.AddWithValue("$appUserId", appUserId.Value);

		await using var reader = await command.ExecuteReaderAsync();
		_ = await reader.ReadAsync();
		var enabled = reader.GetBoolean(0);
		var keyProtected = reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1);

		return (enabled, keyProtected);
	}

	private async Task<(string CookieHeader, string Token)> GetManageAccountFormAsync(string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Admin/ManageEmployeeAccount");
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in ManageEmployeeAccount page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in ManageEmployeeAccount page body.");

		return (ExtractCookiePair(antiforgeryCookie), token);
	}

	private async Task<HttpResponseMessage> PostLoginAsync(string userName, string password)
	{
		var (antiforgeryCookie, token) = await GetLoginFormAsync();

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = userName,
			["Input.Password"] = password,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetLoginFormAsync()
	{
		var response = await client.GetAsync("/Account/Login");
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in login page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in login page body.");

		return (ExtractCookiePair(antiforgeryCookie), token);
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

	private static string? FindSetCookie(HttpResponseMessage response, string nameContains) =>
		response.Headers.TryGetValues("Set-Cookie", out var values)
			? values.FirstOrDefault(value => value.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
			: null;

	private static string ExtractCookiePair(string setCookieHeader) => setCookieHeader.Split(';')[0];

	/// <summary>
	///     Follows a redirect response, carrying forward any cookie the redirect itself set (notably
	///     the TempData cookie a mutating handler's <c>SuccessMessage</c>/<c>ErrorMessage</c> rides in
	///     on) alongside the caller's own auth cookie.
	/// </summary>
	private async Task<HttpResponseMessage> FollowRedirectAsync(HttpResponseMessage response, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, response.Headers.Location);
		var cookieHeader = string.Join("; ", new[] { authCookie }.Concat(ExtractSetCookiePairs(response)));
		request.Headers.Add("Cookie", cookieHeader);

		return await client.SendAsync(request);
	}

	private static IEnumerable<string> ExtractSetCookiePairs(HttpResponseMessage response) =>
		response.Headers.TryGetValues("Set-Cookie", out var values) ? values.Select(ExtractCookiePair) : [];

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

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

	private async Task<bool> IsEnabledAsync(AppUserId appUserId)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT is_enabled FROM identity_user WHERE app_user_id = $appUserId;";
		_ = command.Parameters.AddWithValue("$appUserId", appUserId.Value);

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) == 1;
	}

	private async Task<decimal> GetDefaultHourlyRateAsync(AppUserId appUserId)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT default_hourly_rate FROM app_user WHERE id = $appUserId;";
		_ = command.Parameters.AddWithValue("$appUserId", appUserId.Value);

		return decimal.Parse((string)(await command.ExecuteScalarAsync())!, CultureInfo.InvariantCulture);
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
