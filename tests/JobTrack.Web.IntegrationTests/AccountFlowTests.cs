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
using TestSupport;
using Program = Program;

/// <summary>
///     §8.5 slice 1 (sign-in, forced password change, logout, access-denied handling) exercised over
///     real HTTP against a schema-deployed SQLite database — direct-request tests, not hidden-UI-control
///     tests (plan §8.3), covering threat-model rows 1, 2, 4 (ADR/threat-model doc §3).
/// </summary>
public sealed partial class AccountFlowTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const int MaxFailedAccessAttempts = 5;

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
	public async Task Signing_in_with_valid_credentials_redirects_and_sets_a_secure_httponly_samesite_cookie()
	{
		var appUserId = await SeedUserAsync("ada", KnownPassword, false);

		var response = await PostLoginAsync("ada", KnownPassword);
		var auditOperation = await GetLatestAuditOperationAsync(appUserId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var authCookie = FindSetCookie(response, "Identity.Application");
		authCookie.Should().NotBeNull();
		authCookie.Should().ContainEquivalentOf("secure");
		authCookie.Should().ContainEquivalentOf("httponly");
		// Lax (not Strict) so the cookie survives externally-initiated top-level navigations -- a
		// password manager opening the saved URL, following an emailed link, or the post-login
		// redirect itself -- which is what left users bounced back to the login page. CSRF on
		// state-changing requests stays covered by the antiforgery token, not the cookie's SameSite.
		authCookie.Should().ContainEquivalentOf("samesite=lax");
		auditOperation.Should().Be("authentication.login-success");
	}

	[Fact]
	public async Task Signing_in_with_an_unknown_username_and_a_wrong_password_produce_the_identical_generic_failure()
	{
		var appUserId = await SeedUserAsync("grace", KnownPassword, false);

		var unknownUserResponse = await PostLoginAsync("no-such-user", KnownPassword);
		var wrongPasswordResponse = await PostLoginAsync("grace", "wrong-password");
		var auditOperation = await GetLatestAuditOperationAsync(appUserId);
		var unknownUserAudit = await GetLatestUnknownLoginFailureAuditAsync();

		var unknownUserBody = await unknownUserResponse.Content.ReadAsStringAsync();
		var wrongPasswordBody = await wrongPasswordResponse.Content.ReadAsStringAsync();

		unknownUserResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		wrongPasswordResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		ExtractGenericFailureMessage(unknownUserBody).Should().Be(ExtractGenericFailureMessage(wrongPasswordBody));
		auditOperation.Should().Be("authentication.login-failed");
		unknownUserAudit.Operation.Should().Be("authentication.login-failed");
		unknownUserAudit.AfterData.Should().Contain("redacted");
		unknownUserAudit.AfterData.Should().NotContain("no-such-user");
	}

	/// <summary>
	///     §2.6: an administrator's choice of an employee's display name is ordinary, non-unique user
	///     data (spec §16) -- it must never let an unrelated anonymous login failure attach to that
	///     employee, which is exactly what the removed display-name-keyed "system actor" lookup allowed.
	/// </summary>
	[Fact]
	public async Task An_employee_sharing_the_former_system_actor_display_name_is_never_blamed_for_an_unknown_login_failure()
	{
		var collidingActorId = await SeedAppUserOnlyAsync("JobTrack authentication audit");

		_ = await PostLoginAsync("no-such-user", KnownPassword);
		var (actorUserId, entityType) = await GetLatestUnknownLoginFailureActorAsync();

		actorUserId.Should().BeNull();
		actorUserId.Should().NotBe(collidingActorId.Value);
		entityType.Should().Be("authentication_attempt");
	}

	/// <summary>
	///     §2.6: simultaneous unknown-username failures must all be recorded, must not fail the request,
	///     and must never race to create or share an actor row -- there is none to create now that
	///     <c>actor_user_id</c> is simply left null.
	/// </summary>
	[Fact]
	public async Task Simultaneous_unknown_login_failures_are_all_recorded_without_any_request_failing()
	{
		const int concurrentAttempts = 8;

		var responses = await Task.WhenAll(Enumerable.Range(0, concurrentAttempts).Select(_ => PostLoginAsync("no-such-user", KnownPassword)));

		responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
		(await CountUnknownLoginFailuresWithNullActorAsync()).Should().Be(concurrentAttempts);
	}

	[Fact]
	public async Task Repeated_failed_attempts_lock_the_account_and_reject_the_correct_password_once_locked()
	{
		var appUserId = await SeedUserAsync("katherine", KnownPassword, false);

		for (var attempt = 0; attempt < MaxFailedAccessAttempts; attempt++) {
			_ = await PostLoginAsync("katherine", "wrong-password");
		}

		var lockedOutAttempt = await PostLoginAsync("katherine", KnownPassword);
		var auditOperation = await GetLatestAuditOperationAsync(appUserId);

		lockedOutAttempt.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await lockedOutAttempt.Content.ReadAsStringAsync();
		ExtractGenericFailureMessage(body).Should().NotBeNullOrEmpty();
		auditOperation.Should().Be("authentication.lockout");
	}

	[Fact]
	public async Task Posting_the_login_form_without_the_antiforgery_token_is_rejected()
	{
		await SeedUserAsync("margaret", KnownPassword, false);
		var (antiforgeryCookie, _) = await GetLoginFormAsync();

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		request.Headers.Add("Cookie", antiforgeryCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = "margaret",
			["Input.Password"] = KnownPassword,
		});

		var response = await client.SendAsync(request);

		// Rejection is now graceful: a 302 back to a fresh login form rather than a zero-byte 400 that
		// browsers replay on refresh. The CSRF guarantee is unchanged -- no credentials are processed,
		// so no authentication cookie is issued for a forged (tokenless) login POST.
		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
		FindSetCookie(response, "Identity.Application").Should().BeNull();
	}

	[Fact]
	public async Task A_stale_antiforgery_token_redirects_to_a_fresh_login_form_showing_a_retry_notice()
	{
		await SeedUserAsync("dorothy", KnownPassword, false);
		var (antiforgeryCookie, _) = await GetLoginFormAsync();

		// A valid antiforgery cookie paired with a garbage request token fails validation exactly as a
		// key rotated by a scale-to-zero cold start would -- the real-world trigger this fix targets.
		using var postRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
		postRequest.Headers.Add("Cookie", antiforgeryCookie);
		postRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Input.UserName"] = "dorothy",
			["Input.Password"] = KnownPassword,
			["__RequestVerificationToken"] = "stale-token-value",
		});
		var postResponse = await client.SendAsync(postRequest);

		postResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		postResponse.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
		var tempDataCookie = ExtractCookiePair(FindSetCookie(postResponse, "TempData")
											   ?? throw new InvalidOperationException("No TempData cookie carrying the retry notice."));

		using var followRequest = new HttpRequestMessage(HttpMethod.Get, postResponse.Headers.Location!.OriginalString);
		followRequest.Headers.Add("Cookie", tempDataCookie);
		var followResponse = await client.SendAsync(followRequest);
		var body = await followResponse.Content.ReadAsStringAsync();

		followResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Your session expired");
	}

	[Fact]
	public async Task Signing_in_with_a_forced_password_change_flag_redirects_to_change_password()
	{
		await SeedUserAsync("margaret-hamilton", KnownPassword, true);

		var response = await PostLoginAsync("margaret-hamilton", KnownPassword);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location.Should().NotBeNull();
		response.Headers.Location!.OriginalString.Should().Contain("/Account/ChangePassword");
	}

	[Fact]
	public async Task A_requester_with_a_forced_password_change_can_reach_the_change_password_page()
	{
		await SeedUserAsync("rita.requester", KnownPassword, true, EmployeeRole.Requester);

		var response = await PostLoginAsync("rita.requester", KnownPassword);
		var authCookie = ExtractCookiePair(FindSetCookie(response, "Identity.Application")!);

		using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/ChangePassword");
		request.Headers.Add("Cookie", authCookie);
		var pageResponse = await client.SendAsync(request);

		pageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Requesting_the_login_page_while_already_authenticated_redirects_into_the_app()
	{
		await SeedUserAsync("linus", KnownPassword, false);
		var loginResponse = await PostLoginAsync("linus", KnownPassword);
		var authCookie = ExtractCookiePair(FindSetCookie(loginResponse, "Identity.Application")!);

		using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/Login");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().NotContain("/Account/Login");
		response.Headers.Location!.OriginalString.Should().Be("/");
	}

	[Fact]
	public async Task An_unauthenticated_request_for_a_protected_page_redirects_to_sign_in()
	{
		var response = await client.GetAsync("/Account/PersonalAccessTokens");

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
	}

	[Fact]
	public async Task The_access_denied_page_renders_without_requiring_authentication_details()
	{
		var response = await client.GetAsync("/Account/AccessDenied");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Signing_out_clears_the_authentication_cookie()
	{
		var appUserId = await SeedUserAsync("hopper", KnownPassword, false);
		var loginResponse = await PostLoginAsync("hopper", KnownPassword);
		var authCookie = ExtractCookiePair(FindSetCookie(loginResponse, "Identity.Application")!);

		// The antiforgery token embeds the caller's authentication state at issuance time (session-
		// fixation protection) — fetch it with the auth cookie attached so it matches the POST below.
		using var getLogoutRequest = new HttpRequestMessage(HttpMethod.Get, "/Account/Logout");
		getLogoutRequest.Headers.Add("Cookie", authCookie);
		var getLogoutResponse = await client.SendAsync(getLogoutRequest);
		var logoutBody = await getLogoutResponse.Content.ReadAsStringAsync();
		var antiforgeryCookie = ExtractCookiePair(FindSetCookie(getLogoutResponse, "Antiforgery") ??
												  throw new InvalidOperationException("No antiforgery cookie in logout page response."));
		var token = AntiforgeryTokenPattern().Match(logoutBody) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in logout page body.");

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Logout");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token });

		var response = await client.SendAsync(request);
		var auditOperation = await GetLatestAuditOperationAsync(appUserId);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var clearedCookie = FindSetCookie(response, "Identity.Application");
		clearedCookie.Should().NotBeNull();
		clearedCookie.Should().Contain("01 Jan 1970");
		auditOperation.Should().Be("authentication.logout");
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

	private static string? FindSetCookie(HttpResponseMessage response, string nameContains) =>
		response.Headers.TryGetValues("Set-Cookie", out var values)
			? values.FirstOrDefault(value => value.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
			: null;

	private static string ExtractCookiePair(string setCookieHeader) => setCookieHeader.Split(';')[0];

	private static string ExtractGenericFailureMessage(string html) =>
		FailureMessagePattern().Match(html) is { Success: true } match ? match.Groups["message"].Value : string.Empty;

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

	private async Task<(string Operation, string AfterData)> GetLatestUnknownLoginFailureAuditAsync()
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT operation, after_data
							  FROM audit_event
							  WHERE entity_type = 'authentication_attempt'
							  ORDER BY id DESC
							  LIMIT 1;
							  """;

		await using var reader = await command.ExecuteReaderAsync();
		(await reader.ReadAsync()).Should().BeTrue();
		return (reader.GetString(0), reader.GetString(1));
	}

	private async Task<(long? ActorUserId, string EntityType)> GetLatestUnknownLoginFailureActorAsync()
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT actor_user_id, entity_type
							  FROM audit_event
							  WHERE entity_type = 'authentication_attempt'
							  ORDER BY id DESC
							  LIMIT 1;
							  """;

		await using var reader = await command.ExecuteReaderAsync();
		(await reader.ReadAsync()).Should().BeTrue();
		return (reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.GetString(1));
	}

	private async Task<long> CountUnknownLoginFailuresWithNullActorAsync()
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT COUNT(*)
							  FROM audit_event
							  WHERE entity_type = 'authentication_attempt' AND actor_user_id IS NULL;
							  """;

		return (long)(await command.ExecuteScalarAsync())!;
	}

	private async Task<AppUserId> SeedAppUserOnlyAsync(string displayName)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var insertAppUser = connection.CreateCommand();
		insertAppUser.CommandText =
			"INSERT INTO app_user (display_name, iana_time_zone) VALUES ($displayName, 'UTC'); SELECT last_insert_rowid();";
		_ = insertAppUser.Parameters.AddWithValue("$displayName", displayName);

		return new((long)(await insertAppUser.ExecuteScalarAsync())!);
	}

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	[GeneratedRegex("role=\"alert\">(?<message>[^<]+)<")]
	private static partial Regex FailureMessagePattern();

	private async Task<AppUserId> SeedUserAsync(string userName, string password, bool requiresPasswordChange,
		EmployeeRole role = EmployeeRole.Worker)
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
										 	 $concurrencyStamp, $requiresPasswordChange, 1, 1, 0);
										 """;
		_ = insertIdentityUser.Parameters.AddWithValue("$appUserId", appUserId);
		_ = insertIdentityUser.Parameters.AddWithValue("$userName", userName);
		_ = insertIdentityUser.Parameters.AddWithValue("$normalizedUserName", userName.ToUpperInvariant());
		_ = insertIdentityUser.Parameters.AddWithValue("$passwordHash", passwordHash);
		_ = insertIdentityUser.Parameters.AddWithValue("$securityStamp", placeholderUser.SecurityStamp);
		_ = insertIdentityUser.Parameters.AddWithValue("$concurrencyStamp", placeholderUser.ConcurrencyStamp);
		_ = insertIdentityUser.Parameters.AddWithValue("$requiresPasswordChange", requiresPasswordChange ? 1 : 0);
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
			// Program.cs reads Database:Provider/ConnectionStrings:JobTrackIdentity from
			// WebApplicationBuilder.Configuration before Build() runs — UseSetting applies early
			// enough for that read; ConfigureAppConfiguration (applied during Build()) is too late.
			_ = builder.UseEnvironment("Development");
			_ = builder.UseSetting("Database:Provider", "Sqlite");
			_ = builder.UseSetting("ConnectionStrings:JobTrackIdentity", identityConnectionString);
		}
	}
}
