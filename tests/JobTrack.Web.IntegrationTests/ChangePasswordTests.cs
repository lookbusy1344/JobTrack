namespace JobTrack.Web.IntegrationTests;

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
using NodaTime;
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     TC-WEB-AUTHN-005 (threat model row 3): self-service password change revokes every other live
///     session for the same employee, since <c>UserManager.ChangePasswordAsync</c> rotates the security
///     stamp every cookie is validated against. The changing session itself keeps working because
///     <see cref="Pages.Account.ChangePasswordModel.OnPostAsync" /> calls
///     <c>SignInManager.RefreshSignInAsync</c> to re-issue its own cookie against the new stamp.
/// </summary>
public sealed partial class ChangePasswordTests : IAsyncLifetime, IDisposable
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
	public async Task Changing_a_password_ends_the_employees_other_live_sessions_on_their_next_request()
	{
		await SeedUserAsync("irene", KnownPassword);
		var firstSessionCookie = await SignInAsync("irene", KnownPassword);
		var otherSessionCookie = await SignInAsync("irene", KnownPassword);

		var (antiforgeryCookie, token) = await GetChangePasswordFormAsync(firstSessionCookie);
		using var changeRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/ChangePassword");
		changeRequest.Headers.Add("Cookie", $"{firstSessionCookie}; {antiforgeryCookie}");
		changeRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.CurrentPassword"] = KnownPassword,
			["Input.NewPassword"] = NewPassword,
			["Input.ConfirmNewPassword"] = NewPassword,
			["__RequestVerificationToken"] = token,
		});
		var changeResponse = await client.SendAsync(changeRequest);
		changeResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

		using var otherSessionRequest = new HttpRequestMessage(HttpMethod.Get, "/Account/PersonalAccessTokens");
		otherSessionRequest.Headers.Add("Cookie", otherSessionCookie);
		var otherSessionResponse = await client.SendAsync(otherSessionRequest);

		otherSessionResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		otherSessionResponse.Headers.Location!.OriginalString.Should().Contain("/Account/Login");

		var refreshedCookie = FindSetCookie(changeResponse, "Identity.Application");
		refreshedCookie.Should().NotBeNull();
		using var changingSessionRequest = new HttpRequestMessage(HttpMethod.Get, "/Account/PersonalAccessTokens");
		changingSessionRequest.Headers.Add("Cookie", ExtractCookiePair(refreshedCookie!));
		var changingSessionResponse = await client.SendAsync(changingSessionRequest);

		changingSessionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Accepts_a_new_password_using_only_lowercase_letters_and_digits_at_the_minimum_length()
	{
		await SeedUserAsync("frankie", KnownPassword);
		var authCookie = await SignInAsync("frankie", KnownPassword);
		var (antiforgeryCookie, token) = await GetChangePasswordFormAsync(authCookie);
		const string relaxedPassword = "abc123";

		using var changeRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/ChangePassword");
		changeRequest.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		changeRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.CurrentPassword"] = KnownPassword,
			["Input.NewPassword"] = relaxedPassword,
			["Input.ConfirmNewPassword"] = relaxedPassword,
			["__RequestVerificationToken"] = token,
		});
		var changeResponse = await client.SendAsync(changeRequest);

		changeResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
	}

	[Fact]
	public async Task Rejects_a_new_password_below_the_minimum_length()
	{
		await SeedUserAsync("harriet", KnownPassword);
		var authCookie = await SignInAsync("harriet", KnownPassword);
		var (antiforgeryCookie, token) = await GetChangePasswordFormAsync(authCookie);
		const string tooShortPassword = "abc12";

		using var changeRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/ChangePassword");
		changeRequest.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		changeRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.CurrentPassword"] = KnownPassword,
			["Input.NewPassword"] = tooShortPassword,
			["Input.ConfirmNewPassword"] = tooShortPassword,
			["__RequestVerificationToken"] = token,
		});
		var changeResponse = await client.SendAsync(changeRequest);
		var body = await changeResponse.Content.ReadAsStringAsync();

		changeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("alert-danger", "an Identity rejection (not tied to one field) renders through the same bubble every other page uses");
	}

	[Fact]
	public async Task A_missing_current_password_shows_an_inline_field_error_not_a_bubble()
	{
		await SeedUserAsync("jocasta", KnownPassword);
		var authCookie = await SignInAsync("jocasta", KnownPassword);
		var (antiforgeryCookie, token) = await GetChangePasswordFormAsync(authCookie);

		using var changeRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/ChangePassword");
		changeRequest.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		changeRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.CurrentPassword"] = string.Empty,
			["Input.NewPassword"] = "Some-New-Horse-1!",
			["Input.ConfirmNewPassword"] = "Some-New-Horse-1!",
			["__RequestVerificationToken"] = token,
		});
		var changeResponse = await client.SendAsync(changeRequest);
		var body = await changeResponse.Content.ReadAsStringAsync();

		changeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain(
			"span class=\"text-danger field-validation-error\" data-valmsg-for=\"Input.CurrentPassword\"",
			"a field-level error stays next to its own field, not folded into the page-level bubble");
		body.Should().NotContain("alert-danger", "a plain missing-field error never reached the OnPostAsync handler, so no bubble should render");
	}

	[Fact]
	public async Task Rejects_a_new_password_with_no_letters()
	{
		await SeedUserAsync("isolde", KnownPassword);
		var authCookie = await SignInAsync("isolde", KnownPassword);
		var (antiforgeryCookie, token) = await GetChangePasswordFormAsync(authCookie);
		const string digitsOnlyPassword = "123456";

		using var changeRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/ChangePassword");
		changeRequest.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		changeRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.CurrentPassword"] = KnownPassword,
			["Input.NewPassword"] = digitsOnlyPassword,
			["Input.ConfirmNewPassword"] = digitsOnlyPassword,
			["__RequestVerificationToken"] = token,
		});
		var changeResponse = await client.SendAsync(changeRequest);

		changeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Changing_a_password_revokes_the_employees_personal_access_tokens()
	{
		var appUserId = await SeedUserAsync("gracie", KnownPassword);
		var seedClient = JobTrackSqlite.Create(database.ConnectionString);
		var issued = await seedClient.Tokens.IssueAsync(new() {
			Context = new() { Actor = appUserId, CorrelationId = Guid.NewGuid() },
			TargetUserId = appUserId,
			Label = "cli-test-token",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});
		var authCookie = await SignInAsync("gracie", KnownPassword);
		var (antiforgeryCookie, token) = await GetChangePasswordFormAsync(authCookie);
		using var changeRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/ChangePassword");
		changeRequest.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		changeRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.CurrentPassword"] = KnownPassword,
			["Input.NewPassword"] = NewPassword,
			["Input.ConfirmNewPassword"] = NewPassword,
			["__RequestVerificationToken"] = token,
		});
		var changeResponse = await client.SendAsync(changeRequest);
		changeResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

		using var apiRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/employees/{appUserId.Value}/rates");
		apiRequest.Headers.Authorization = new("Bearer", issued.Token);
		var apiResponse = await client.SendAsync(apiRequest);

		apiResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Changing_a_password_writes_an_audit_event()
	{
		var appUserId = await SeedUserAsync("helen", KnownPassword);
		var authCookie = await SignInAsync("helen", KnownPassword);
		var (antiforgeryCookie, token) = await GetChangePasswordFormAsync(authCookie);

		using var changeRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/ChangePassword");
		changeRequest.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		changeRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.CurrentPassword"] = KnownPassword,
			["Input.NewPassword"] = NewPassword,
			["Input.ConfirmNewPassword"] = NewPassword,
			["__RequestVerificationToken"] = token,
		});
		var changeResponse = await client.SendAsync(changeRequest);
		var auditOperation = await GetLatestAuditOperationAsync(appUserId);

		changeResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		auditOperation.Should().Be("authentication.password-change");
	}

	[Fact]
	public async Task A_requester_can_change_their_password()
	{
		await SeedUserAsync("rita.requester", KnownPassword, EmployeeRole.Requester);
		var authCookie = await SignInAsync("rita.requester", KnownPassword);
		var (antiforgeryCookie, token) = await GetChangePasswordFormAsync(authCookie);

		using var changeRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/ChangePassword");
		changeRequest.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		changeRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.CurrentPassword"] = KnownPassword,
			["Input.NewPassword"] = NewPassword,
			["Input.ConfirmNewPassword"] = NewPassword,
			["__RequestVerificationToken"] = token,
		});
		var changeResponse = await client.SendAsync(changeRequest);

		changeResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
	}

	private async Task<string> SignInAsync(string userName, string password)
	{
		var (antiforgeryCookie, token) = await GetLoginFormAsync();

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = userName,
			["Input.Password"] = password,
			["__RequestVerificationToken"] = token,
		});

		var response = await client.SendAsync(request);
		return ExtractCookiePair(FindSetCookie(response, "Identity.Application") ??
								 throw new InvalidOperationException("Sign-in did not set the authentication cookie."));
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

	private async Task<(string CookieHeader, string Token)> GetChangePasswordFormAsync(string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/ChangePassword");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in change-password page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in change-password page body.");

		return (ExtractCookiePair(antiforgeryCookie), token);
	}

	private static string? FindSetCookie(HttpResponseMessage response, string nameContains) =>
		response.Headers.TryGetValues("Set-Cookie", out var values)
			? values.FirstOrDefault(value => value.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
			: null;

	private static string ExtractCookiePair(string setCookieHeader) => setCookieHeader.Split(';')[0];

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	private async Task<string> GetLatestAuditOperationAsync(AppUserId actorUserId)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT operation
							  FROM audit_event
							  WHERE actor_user_id = $actorUserId
							  ORDER BY id DESC
							  LIMIT 1;
							  """;
		_ = command.Parameters.AddWithValue("$actorUserId", actorUserId.Value);

		return (string)(await command.ExecuteScalarAsync())!;
	}

	private async Task<AppUserId> SeedUserAsync(string userName, string password, EmployeeRole role = EmployeeRole.Worker)
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
		var passwordHash = new PasswordHasher<JobTrackIdentityUser>().HashPassword(placeholderUser, password);

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
