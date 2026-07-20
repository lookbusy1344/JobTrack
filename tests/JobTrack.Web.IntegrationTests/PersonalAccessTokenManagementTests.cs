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
///     Direct-HTTP tests for the self-service personal access token page (security review remediation
///     §2.2): issue/list/revoke as the owner, administrator revoke-all from the employee-management
///     page, CSRF denial, and the "shown once, never cached" contract for the plaintext token.
/// </summary>
public sealed partial class PersonalAccessTokenManagementTests : IAsyncLifetime, IDisposable
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
	public async Task A_worker_can_issue_a_token_for_themselves_and_it_is_shown_exactly_once()
	{
		_ = await SeedEmployeeAsync("pat.issue", EmployeeRole.Worker);
		var authCookie = await SignInAsync("pat.issue");

		var issueResponse = await PostIssueAsync(authCookie, "laptop", 30);
		issueResponse.StatusCode.Should().Be(HttpStatusCode.Redirect, "PRG: a successful mutating POST never renders the result directly");
		var issueLocation = issueResponse.Headers.Location!.OriginalString;
		issueLocation.Should().NotContain("jtpat_", "the plaintext token is never carried in a URL");

		var revealResponse = await FollowRedirectAsync(issueResponse, authCookie);
		var revealBody = await revealResponse.Content.ReadAsStringAsync();

		revealResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		revealBody.Should().Contain("jtpat_");
		revealBody.Should().Contain("laptop");

		var refreshResponse = await FollowRedirectAsync(issueResponse, authCookie);
		var refreshBody = await refreshResponse.Content.ReadAsStringAsync();

		refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		refreshBody.Should().NotContain("jtpat_", "the one-use delivery slot was already consumed by the first GET");
		refreshBody.Should().Contain("no longer available");

		var listResponse = await GetPageAsync(authCookie);
		var listBody = await listResponse.Content.ReadAsStringAsync();

		listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		listBody.Should().NotContain("jtpat_", "the plaintext token is never shown again once its delivery slot is consumed");
		listBody.Should().Contain("laptop");
	}

	[Fact]
	public async Task Refreshing_the_issuance_redirect_does_not_mint_a_second_token()
	{
		_ = await SeedEmployeeAsync("pat.refresh", EmployeeRole.Worker);
		var authCookie = await SignInAsync("pat.refresh");

		var issueResponse = await PostIssueAsync(authCookie, "laptop", 30);
		_ = await FollowRedirectAsync(issueResponse, authCookie);
		_ = await FollowRedirectAsync(issueResponse, authCookie);

		var tokenCount = await CountTokensAsync("pat.refresh");
		tokenCount.Should().Be(1, "resubmitting the same redirect GET must never mint an additional credential");
	}

	[Fact]
	public async Task Issued_tokens_are_never_returned_from_a_cached_response()
	{
		_ = await SeedEmployeeAsync("pat.no-cache", EmployeeRole.Worker);
		var authCookie = await SignInAsync("pat.no-cache");

		var issueResponse = await PostIssueAsync(authCookie, "laptop", 30);
		issueResponse.Headers.CacheControl.Should().NotBeNull();
		issueResponse.Headers.CacheControl!.NoStore.Should().BeTrue();

		var revealResponse = await FollowRedirectAsync(issueResponse, authCookie);
		revealResponse.Headers.CacheControl.Should().NotBeNull();
		revealResponse.Headers.CacheControl!.NoStore.Should().BeTrue();
	}

	[Fact]
	public async Task A_worker_can_revoke_their_own_token()
	{
		_ = await SeedEmployeeAsync("pat.revoke", EmployeeRole.Worker);
		var authCookie = await SignInAsync("pat.revoke");
		_ = await PostIssueAsync(authCookie, "to-revoke", 30);
		var tokenId = await GetMostRecentTokenIdAsync("pat.revoke");

		var revokeResponse = await PostRevokeAsync(authCookie, tokenId);
		revokeResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var revokeReloaded = await FollowRedirectAsync(revokeResponse, authCookie);
		var revokeBody = await revokeReloaded.Content.ReadAsStringAsync();

		revokeBody.Should().Contain("revoked");
	}

	/// <summary>
	///     The handler always scopes <c>TargetUserId</c> to the signed-in actor (never a caller-supplied
	///     value), so another user's <c>tokenId</c> simply matches no row for the attacker's own scope --
	///     it fails closed as "not found" without ever revealing whether the token exists for someone
	///     else, and critically, the owner's token is left untouched.
	/// </summary>
	[Fact]
	public async Task A_worker_cannot_revoke_another_workers_token()
	{
		_ = await SeedEmployeeAsync("pat.revoke.owner", EmployeeRole.Worker);
		var otherAuthCookie = await SignInAsync("pat.revoke.owner");
		_ = await PostIssueAsync(otherAuthCookie, "owner-token", 30);
		var tokenId = await GetMostRecentTokenIdAsync("pat.revoke.owner");

		_ = await SeedEmployeeAsync("pat.revoke.attacker", EmployeeRole.Worker);
		var attackerAuthCookie = await SignInAsync("pat.revoke.attacker");

		var revokeResponse = await PostRevokeAsync(attackerAuthCookie, tokenId);
		revokeResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var revokeReloaded = await FollowRedirectAsync(revokeResponse, attackerAuthCookie);
		var revokeBody = await revokeReloaded.Content.ReadAsStringAsync();

		revokeBody.Should().Contain("does not exist");
		(await IsTokenRevokedAsync(tokenId)).Should().BeFalse("the attacker's request must not affect the owner's token");
	}

	[Fact]
	public async Task Issuing_a_token_without_an_antiforgery_token_is_rejected()
	{
		_ = await SeedEmployeeAsync("pat.csrf", EmployeeRole.Worker);
		var authCookie = await SignInAsync("pat.csrf");

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/PersonalAccessTokens?handler=Issue");
		request.Headers.Add("Cookie", authCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Issue.Label"] = "no-antiforgery",
			["Issue.LifetimeDays"] = "30",
		});
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task An_administrator_can_revoke_all_of_an_employees_tokens()
	{
		var workerId = await SeedEmployeeAsync("pat.admin-revoke.worker", EmployeeRole.Worker);
		_ = await SeedEmployeeAsync("pat.admin-revoke.admin", EmployeeRole.Administrator);
		var workerAuthCookie = await SignInAsync("pat.admin-revoke.worker");
		_ = await PostIssueAsync(workerAuthCookie, "worker-token", 30);
		var adminAuthCookie = await SignInAsync("pat.admin-revoke.admin");

		var response = await PostAdminRevokeAllAsync(adminAuthCookie, workerId);
		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, adminAuthCookie);
		var body = await reloaded.Content.ReadAsStringAsync();

		body.Should().Contain("revoked");
	}

	[Fact]
	public async Task A_non_administrator_cannot_revoke_all_of_another_employees_tokens()
	{
		var targetId = await SeedEmployeeAsync("pat.admin-revoke-denied.target", EmployeeRole.Worker);
		_ = await SeedEmployeeAsync("pat.admin-revoke-denied.worker", EmployeeRole.Worker);
		var workerAuthCookie = await SignInAsync("pat.admin-revoke-denied.worker");

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/ManageEmployeeAccount?handler=RevokeAllTokens");
		request.Headers.Add("Cookie", workerAuthCookie);
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["RevokeAllTokens.TargetUserId"] = targetId.Value.ToString(CultureInfo.InvariantCulture),
		});
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		response.Headers.Location!.OriginalString.Should().Contain("/Account/AccessDenied");
	}

	[Fact]
	public async Task A_requester_can_manage_their_own_personal_access_tokens()
	{
		_ = await SeedEmployeeAsync("pat.requester", EmployeeRole.Requester);
		var authCookie = await SignInAsync("pat.requester");

		var response = await GetPageAsync(authCookie);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Your tokens");
	}

	private async Task<HttpResponseMessage> PostIssueAsync(string authCookie, string label, int lifetimeDays)
	{
		var (antiforgeryCookie, token) = await GetTokensFormAsync(authCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/PersonalAccessTokens?handler=Issue");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Issue.Label"] = label,
			["Issue.LifetimeDays"] = lifetimeDays.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostRevokeAsync(string authCookie, long tokenId)
	{
		var (antiforgeryCookie, token) = await GetTokensFormAsync(authCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/PersonalAccessTokens?handler=Revoke");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["tokenId"] = tokenId.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostAdminRevokeAllAsync(string authCookie, AppUserId targetId)
	{
		var (antiforgeryCookie, token) = await GetManageAccountFormAsync(authCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Admin/ManageEmployeeAccount?handler=RevokeAllTokens");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["RevokeAllTokens.TargetUserId"] = targetId.Value.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

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

	private async Task<HttpResponseMessage> GetPageAsync(string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/PersonalAccessTokens");
		request.Headers.Add("Cookie", authCookie);
		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetTokensFormAsync(string authCookie)
	{
		var response = await GetPageAsync(authCookie);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in PersonalAccessTokens page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in PersonalAccessTokens page body.");

		return (ExtractCookiePair(antiforgeryCookie), token);
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

	private async Task<long> GetMostRecentTokenIdAsync(string userName)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT pat.id FROM personal_access_token pat
							  JOIN app_user au ON au.id = pat.app_user_id
							  WHERE au.display_name = $userName
							  ORDER BY pat.created_at DESC
							  LIMIT 1;
							  """;
		_ = command.Parameters.AddWithValue("$userName", userName);

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<long> CountTokensAsync(string userName)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  SELECT COUNT(*) FROM personal_access_token pat
							  JOIN app_user au ON au.id = pat.app_user_id
							  WHERE au.display_name = $userName;
							  """;
		_ = command.Parameters.AddWithValue("$userName", userName);

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<bool> IsTokenRevokedAsync(long tokenId)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT revoked_at FROM personal_access_token WHERE id = $tokenId;";
		_ = command.Parameters.AddWithValue("$tokenId", tokenId);

		return await command.ExecuteScalarAsync() is not DBNull and not null;
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
