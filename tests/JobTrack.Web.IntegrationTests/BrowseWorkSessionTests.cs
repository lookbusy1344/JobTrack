namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using AwesomeAssertions;
using Database;
using Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     Direct-HTTP tests for the inline Start/Finish controls on <c>/Jobs/Browse</c> (recording work is
///     the app's most common action, so it does not require navigating to <c>/Jobs/Work</c> first).
/// </summary>
public sealed partial class BrowseWorkSessionTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";

	private readonly SqliteDatabaseFixture database = new();
	private AppUserId administratorId;
	private HttpClient client = null!;
	private TestWebApplicationFactory factory = null!;
	private JobNodeId rootId;
	private IJobTrackClient seedClient = null!;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await DeploySchemaAsync();

		seedClient = JobTrackSqlite.Create(database.ConnectionString);
		var bootstrapResult = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.browse-work-tests",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		rootId = bootstrapResult.RootJobNodeId;
		administratorId = bootstrapResult.AdministratorId;

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
	public async Task A_worker_can_start_a_session_inline_from_the_browse_row()
	{
		var workerId = await SeedEmployeeAsync("browse.starter", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Pour foundation");
		var authCookie = await SignInAsync("browse.starter");

		var (cookie, token) = await GetBrowseFormAsync(authCookie);
		var response = await PostStartAsync(authCookie, cookie, token, leaf.Id, null);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Work started");
		body.Should().Contain("Active since");
	}

	[Fact]
	public async Task A_worker_can_start_a_session_with_a_backdated_time_from_the_browse_row()
	{
		var workerId = await SeedEmployeeAsync("browse.backdater", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Frame walls");
		var authCookie = await SignInAsync("browse.backdater");
		var backdated = DateTimeOffset.UtcNow.AddHours(-2).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

		var (cookie, token) = await GetBrowseFormAsync(authCookie);
		var response = await PostStartAsync(authCookie, cookie, token, leaf.Id, backdated);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Work started");
	}

	[Fact]
	public async Task Starting_a_session_with_a_future_time_shows_a_helpful_error()
	{
		var workerId = await SeedEmployeeAsync("browse.future", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Dig trench");
		var authCookie = await SignInAsync("browse.future");
		var future = DateTimeOffset.UtcNow.AddHours(2).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

		var (cookie, token) = await GetBrowseFormAsync(authCookie);
		var response = await PostStartAsync(authCookie, cookie, token, leaf.Id, future);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("in the future");
	}

	[Fact]
	public async Task A_worker_can_finish_their_active_session_inline_from_the_browse_row()
	{
		var workerId = await SeedEmployeeAsync("browse.finisher", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Lay bricks");
		var authCookie = await SignInAsync("browse.finisher");

		var (startCookie, startToken) = await GetBrowseFormAsync(authCookie);
		var startResponse = await PostStartAsync(authCookie, startCookie, startToken, leaf.Id, null);
		var startBody = await startResponse.Content.ReadAsStringAsync();
		var (sessionId, version) = ExtractFirstSession(startBody);

		var (finishCookie, finishToken) = await ExtractFormAsync(startResponse, startCookie);
		var finishResponse = await PostFinishAsync(authCookie, finishCookie, finishToken, sessionId, version, null);
		var finishBody = await finishResponse.Content.ReadAsStringAsync();

		finishResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		finishBody.Should().Contain("Session finished");
	}

	[Fact]
	public async Task A_worker_can_pick_up_an_unassigned_leaf_inline_from_the_browse_row()
	{
		_ = await SeedEmployeeAsync("browse.picker", EmployeeRole.Worker);
		var leaf = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = rootId,
			Description = "Unassigned pool leaf",
			OwnerUserId = null,
			Priority = Priority.Medium,
		});
		var authCookie = await SignInAsync("browse.picker");

		var (cookie, token) = await GetBrowseFormAsync(authCookie);
		var response = await PostPickUpAsync(authCookie, cookie, token, leaf.Id);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Job node claimed");
	}

	[Fact]
	public async Task A_leaf_detail_toolbar_shows_finish_instead_of_start_once_a_session_is_active()
	{
		var workerId = await SeedEmployeeAsync("browse.toolbar", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, workerId, "Toolbar toggle leaf");
		var authCookie = await SignInAsync("browse.toolbar");

		var beforeResponse = await GetLeafDetailAsync(authCookie, leaf.Id);
		var beforeBody = await beforeResponse.Content.ReadAsStringAsync();
		beforeBody.Should().Contain(">Start work<");
		beforeBody.Should().NotContain("Active since");

		var (cookie, token) = await GetBrowseFormAsync(authCookie);
		_ = await PostStartAsync(authCookie, cookie, token, leaf.Id, null);

		var afterResponse = await GetLeafDetailAsync(authCookie, leaf.Id);
		var afterBody = await afterResponse.Content.ReadAsStringAsync();

		afterResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		afterBody.Should().Contain("Active since");
		afterBody.Should().NotContain(">Start work<");
	}

	[Fact]
	public async Task Work_page_exposes_a_worked_by_employee_selector()
	{
		var ownerId = await SeedEmployeeAsync("work.selector.owner", EmployeeRole.Worker);
		var otherWorkerId = await SeedEmployeeAsync("work.selector.other", EmployeeRole.Worker);
		var leaf = await AddWorkedLeafAsync(rootId, ownerId, "Selector leaf");
		var authCookie = await SignInAsync("work.selector.owner");

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Work?leafNodeId={leaf.Id.Value}");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("<select");
		body.Should().Contain("name=\"WorkedByUserId\"");
		body.Should().Contain($"value=\"{otherWorkerId.Value}\">work.selector.other");
	}

	private async Task<HttpResponseMessage> GetLeafDetailAsync(string authCookie, JobNodeId nodeId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Browse?nodeId={nodeId.Value}");
		request.Headers.Add("Cookie", authCookie);

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostPickUpAsync(string authCookie, string antiforgeryCookie, string token, JobNodeId nodeId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Browse?handler=PickUp");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["nodeId"] = nodeId.Value.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<JobNodeResult> AddWorkedLeafAsync(JobNodeId parentId, AppUserId ownerId, string description)
	{
		var leaf = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
		});

		return leaf;
	}

	private async Task<HttpResponseMessage> PostStartAsync(
		string authCookie, string antiforgeryCookie, string token, JobNodeId leafNodeId, string? startedAt)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Browse?handler=Start");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		var fields = new Dictionary<string, string> {
			["leafNodeId"] = leafNodeId.Value.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		};
		if (startedAt is not null) {
			fields["startedAt"] = startedAt;
		}

		request.Content = new FormUrlEncodedContent(fields);

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostFinishAsync(
		string authCookie, string antiforgeryCookie, string token, long sessionId, long version, string? finishedAt)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Jobs/Browse?handler=Finish");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		var fields = new Dictionary<string, string> {
			["sessionId"] = sessionId.ToString(CultureInfo.InvariantCulture),
			["version"] = version.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		};
		if (finishedAt is not null) {
			fields["finishedAt"] = finishedAt;
		}

		request.Content = new FormUrlEncodedContent(fields);

		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetBrowseFormAsync(string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Jobs/Browse");
		request.Headers.Add("Cookie", authCookie);

		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in Browse response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in Browse body.");

		return (ExtractCookiePair(antiforgeryCookie), token);
	}

	private static async Task<(string CookieHeader, string Token)> ExtractFormAsync(HttpResponseMessage response, string previousAntiforgeryCookie)
	{
		var body = await response.Content.ReadAsStringAsync();
		var cookie = FindSetCookie(response, "Antiforgery") is { } newCookie ? ExtractCookiePair(newCookie) : previousAntiforgeryCookie;
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in response body.");

		return (cookie, token);
	}

	private static (long SessionId, long Version) ExtractFirstSession(string body)
	{
		var sessionIdMatch = SessionIdPattern().Match(body);
		var versionMatch = VersionPattern().Match(body);
		if (!sessionIdMatch.Success || !versionMatch.Success) {
			throw new InvalidOperationException("No session row found in Browse page body.");
		}

		return (long.Parse(sessionIdMatch.Groups["id"].Value, CultureInfo.InvariantCulture),
			long.Parse(versionMatch.Groups["version"].Value, CultureInfo.InvariantCulture));
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

	[GeneratedRegex("name=\"sessionId\" value=\"(?<id>[0-9]+)\"")]
	private static partial Regex SessionIdPattern();

	[GeneratedRegex("name=\"version\" value=\"(?<version>[0-9]+)\"")]
	private static partial Regex VersionPattern();

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
