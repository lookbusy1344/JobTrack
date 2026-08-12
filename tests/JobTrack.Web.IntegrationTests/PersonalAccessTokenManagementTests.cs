namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Abstractions;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using TestSupport;

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
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

		factory = new(database.ConnectionString);
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
	public async Task A_worker_can_issue_a_token_for_themselves_and_it_is_shown_exactly_once()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "pat.issue");
		var authCookie = await client.SignInAsync("pat.issue");

		var issueResponse = await PostIssueAsync(authCookie, "laptop", 30);
		issueResponse.StatusCode.Should().Be(HttpStatusCode.Redirect, "PRG: a successful mutating POST never renders the result directly");
		var issueLocation = issueResponse.Headers.Location!.OriginalString;
		issueLocation.Should().NotContain("jtpat_", "the plaintext token is never carried in a URL");

		var revealResponse = await FollowRedirectAsync(issueResponse, authCookie);
		var revealBody = await revealResponse.Content.ReadAsStringAsync();

		revealResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		revealBody.Should().Contain("jtpat_");
		revealBody.Should().Contain("laptop");

		var refreshResponse = await FollowRedirectAsync(issueResponse, authCookie, revealResponse);
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
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "pat.refresh");
		var authCookie = await client.SignInAsync("pat.refresh");

		var issueResponse = await PostIssueAsync(authCookie, "laptop", 30);
		var firstReveal = await FollowRedirectAsync(issueResponse, authCookie);
		_ = await FollowRedirectAsync(issueResponse, authCookie, firstReveal);

		var tokenCount = await CountTokensAsync("pat.refresh");
		tokenCount.Should().Be(1, "resubmitting the same redirect GET must never mint an additional credential");
	}

	[Fact]
	public async Task Issued_tokens_are_never_returned_from_a_cached_response()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "pat.no-cache");
		var authCookie = await client.SignInAsync("pat.no-cache");

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
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "pat.revoke");
		var authCookie = await client.SignInAsync("pat.revoke");
		_ = await PostIssueAsync(authCookie, "to-revoke", 30);
		var tokenId = await GetMostRecentTokenIdAsync("pat.revoke");

		var revokeResponse = await PostRevokeAsync(authCookie, tokenId);
		revokeResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var revokeReloaded = await FollowRedirectAsync(revokeResponse, authCookie);
		var revokeBody = await revokeReloaded.Content.ReadAsStringAsync();

		revokeBody.Should().Contain("revoked");
	}

	[Fact]
	/// <summary>
	/// Revoke is a per-row action, so it is the same icon button every other table row uses -- the
	/// shared remove cross, tinted by `jt-icon-button--danger` rather than given a glyph of its own,
	/// since what separates it from an ordinary remove is consequence, not kind. Each button names
	/// the token it revokes so the accessible names stay distinguishable between rows.
	/// </summary>
	public async Task A_live_token_row_offers_an_icon_revoke_naming_the_token()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "pat.revoke-icon");
		var authCookie = await client.SignInAsync("pat.revoke-icon");
		_ = await PostIssueAsync(authCookie, "workshop-laptop", 30);

		using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/PersonalAccessTokens");
		request.Headers.Add("Cookie", authCookie);
		var body = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

		body.Should().Contain("#jt-icon-remove");
		body.Should().Contain("class=\"jt-icon-button jt-icon-button--danger\" title=\"Revoke token\"");
		body.Should().Contain("Revoke token workshop-laptop");
		body.Should().NotContain(">Revoke</button>");
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
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "pat.revoke.owner");
		var otherAuthCookie = await client.SignInAsync("pat.revoke.owner");
		_ = await PostIssueAsync(otherAuthCookie, "owner-token", 30);
		var tokenId = await GetMostRecentTokenIdAsync("pat.revoke.owner");

		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "pat.revoke.attacker");
		var attackerAuthCookie = await client.SignInAsync("pat.revoke.attacker");

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
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "pat.csrf");
		var authCookie = await client.SignInAsync("pat.csrf");

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
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "pat.admin-revoke.worker");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "pat.admin-revoke.admin", EmployeeRole.Administrator);
		var workerAuthCookie = await client.SignInAsync("pat.admin-revoke.worker");
		_ = await PostIssueAsync(workerAuthCookie, "worker-token", 30);
		var adminAuthCookie = await client.SignInAsync("pat.admin-revoke.admin");

		var response = await PostAdminRevokeAllAsync(adminAuthCookie, workerId);
		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var reloaded = await FollowRedirectAsync(response, adminAuthCookie);
		var body = await reloaded.Content.ReadAsStringAsync();

		body.Should().Contain("revoked");
	}

	[Fact]
	public async Task A_non_administrator_cannot_revoke_all_of_another_employees_tokens()
	{
		var targetId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "pat.admin-revoke-denied.target");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "pat.admin-revoke-denied.worker");
		var workerAuthCookie = await client.SignInAsync("pat.admin-revoke-denied.worker");

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
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "pat.requester", EmployeeRole.Requester);
		var authCookie = await client.SignInAsync("pat.requester");

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
	/// <summary>
	///     Follows <paramref name="response" />'s redirect, carrying forward cookies it set itself
	///     unless <paramref name="cookieSource" /> names a later response to carry forward instead --
	///     a real browser's cookie jar reflects whichever response it saw most recently (e.g. the
	///     delivery-cookie deletion a first reveal GET set), not the original redirecting POST.
	/// </summary>
	private async Task<HttpResponseMessage> FollowRedirectAsync(HttpResponseMessage response, string authCookie,
																HttpResponseMessage? cookieSource = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, response.Headers.Location);
		var cookieHeader = string.Join("; ", new[] {
			authCookie,
		}.Concat(WebTestHttp.ExtractSetCookiePairs(cookieSource ?? response)));
		request.Headers.Add("Cookie", cookieHeader);

		return await client.SendAsync(request);
	}

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
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in PersonalAccessTokens page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in PersonalAccessTokens page body.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}

	private async Task<(string CookieHeader, string Token)> GetManageAccountFormAsync(string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Admin/ManageEmployeeAccount");
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = WebTestHttp.FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in ManageEmployeeAccount page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in ManageEmployeeAccount page body.");

		return (WebTestHttp.ExtractCookiePair(antiforgeryCookie), token);
	}





	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();



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
}
