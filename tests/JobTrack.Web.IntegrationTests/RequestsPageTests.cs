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
///     Direct-HTTP tests for the requester self-service page (ADR 0033, plan §8 <c>/Requests</c>):
///     submitting a request into an eligible holding area, seeing only the requester's own submitted
///     requests, and confirming the Requester role cannot reach the operational job tree.
/// </summary>
public sealed partial class RequestsPageTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const short PriorityMedium = 2;

	private readonly SqliteDatabaseFixture database = new();
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
			UserName = "admin.requests-tests",
			Password = "Bootstrap-Horse-Battery-77!",
			CorrelationId = Guid.NewGuid(),
		});
		rootId = bootstrapResult.RootJobNodeId;

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
	public async Task A_requester_can_submit_a_request_and_see_it_in_their_own_list()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		_ = await SeedEmployeeAsync("rita.requester", EmployeeRole.Requester);
		var authCookie = await SignInAsync("rita.requester");

		var (antiforgeryCookie, token) = await GetPageFormAsync(authCookie);
		var response = await PostSubmitAsync(authCookie, antiforgeryCookie, token, holdingAreaId, "Printer will not turn on");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadAsStringAsync();
		body.Should().Contain("Printer will not turn on");
	}

	[Fact]
	public async Task A_description_containing_script_markup_is_rendered_html_encoded_not_as_live_markup()
	{
		const string InjectedDescription = "<script>alert('xss')</script>";
		var holdingAreaId = await SeedHoldingAreaAsync();
		_ = await SeedEmployeeAsync("rita.xss", EmployeeRole.Requester);
		var authCookie = await SignInAsync("rita.xss");

		var (antiforgeryCookie, token) = await GetPageFormAsync(authCookie);
		var response = await PostSubmitAsync(authCookie, antiforgeryCookie, token, holdingAreaId, InjectedDescription);
		var body = await response.Content.ReadAsStringAsync();

		body.Should().NotContain(InjectedDescription);
		body.Should().Contain("&lt;script&gt;alert(&#x27;xss&#x27;)&lt;/script&gt;");
	}

	[Fact]
	public async Task Submitting_a_blank_description_shows_a_validation_error_and_does_not_submit()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		_ = await SeedEmployeeAsync("rita.blank", EmployeeRole.Requester);
		var authCookie = await SignInAsync("rita.blank");

		var (antiforgeryCookie, token) = await GetPageFormAsync(authCookie);
		var response = await PostSubmitAsync(authCookie, antiforgeryCookie, token, holdingAreaId, string.Empty);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadAsStringAsync();
		body.Should().NotContain("submitted.");
	}

	[Fact]
	public async Task A_requester_does_not_see_another_requesters_submitted_request()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		_ = await SeedEmployeeAsync("rita.owner", EmployeeRole.Requester);
		var ownerCookie = await SignInAsync("rita.owner");
		var (ownerAntiforgery, ownerToken) = await GetPageFormAsync(ownerCookie);
		_ = await PostSubmitAsync(ownerCookie, ownerAntiforgery, ownerToken, holdingAreaId, "Owner's private request");

		_ = await SeedEmployeeAsync("ravi.other", EmployeeRole.Requester);
		var otherCookie = await SignInAsync("ravi.other");

		var otherPage = await GetPageAsync(otherCookie);
		var otherBody = await otherPage.Content.ReadAsStringAsync();

		otherBody.Should().NotContain("Owner's private request");
	}

	[Fact]
	public async Task A_requester_cannot_reach_the_operational_job_browse_page()
	{
		_ = await SeedEmployeeAsync("rita.blocked", EmployeeRole.Requester);
		var authCookie = await SignInAsync("rita.blocked");

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Browse?nodeId={rootId.Value}");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);

		response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Found);
	}

	[Fact]
	public async Task A_worker_cannot_reach_the_requests_page()
	{
		_ = await SeedEmployeeAsync("wanda.worker", EmployeeRole.Worker);
		var authCookie = await SignInAsync("wanda.worker");

		var response = await GetPageAsync(authCookie);

		response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Found);
	}

	[Fact]
	public async Task A_requester_can_view_their_own_request_detail_page()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("rita.detail", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId, "Printer will not turn on");
		var authCookie = await SignInAsync("rita.detail");

		var response = await GetDetailPageAsync(submitted.JobNodeId.Value, authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadAsStringAsync();
		body.Should().Contain("Printer will not turn on");
		body.Should().Contain("Submitted");
	}

	[Fact]
	public async Task A_different_requester_cannot_view_someone_elses_request_detail_page()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("rita.detail-owner", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId, "Owner's private request");
		_ = await SeedEmployeeAsync("ravi.detail-stranger", EmployeeRole.Requester);
		var strangerCookie = await SignInAsync("ravi.detail-stranger");

		var response = await GetDetailPageAsync(submitted.JobNodeId.Value, strangerCookie);

		response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Found);
	}

	[Fact]
	public async Task A_requester_can_add_a_note_from_the_detail_page()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("rita.note", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId, "Printer will not turn on");
		var authCookie = await SignInAsync("rita.note");

		var (antiforgeryCookie, token) = await GetDetailPageFormAsync(submitted.JobNodeId.Value, authCookie);
		var response = await PostAddNoteAsync(submitted.JobNodeId.Value, authCookie, antiforgeryCookie, token, "Any update?");
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Any update?");
	}

	[Fact]
	public async Task A_job_manager_can_acknowledge_a_request_from_the_detail_page()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("rita.for-ack", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId, "Printer will not turn on");
		_ = await SeedEmployeeAsync("priya.jobmanager", EmployeeRole.JobManager);
		var staffCookie = await SignInAsync("priya.jobmanager");

		var (antiforgeryCookie, token) = await GetDetailPageFormAsync(submitted.JobNodeId.Value, staffCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, $"/Requests/{submitted.JobNodeId.Value}?handler=Acknowledge");
		request.Headers.Add("Cookie", $"{staffCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["version"] = submitted.Version.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Acknowledged");
	}

	[Fact]
	public async Task A_job_manager_browsing_the_holding_area_sees_requester_context_for_a_submitted_request()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("rita.triage", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId, "Printer will not turn on");
		_ = await SeedEmployeeAsync("priya.triage-manager", EmployeeRole.JobManager);
		var staffCookie = await SignInAsync("priya.triage-manager");

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Browse?nodeId={submitted.JobNodeId.Value}");
		request.Headers.Add("Cookie", staffCookie);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().Contain("Requester request");
		body.Should().Contain($"/Requests/{submitted.JobNodeId.Value}");
	}

	[Fact]
	public async Task A_job_manager_browsing_an_ordinary_node_sees_no_requester_context()
	{
		_ = await SeedEmployeeAsync("priya.no-request", EmployeeRole.JobManager);
		var staffCookie = await SignInAsync("priya.no-request");

		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Jobs/Browse?nodeId={rootId.Value}");
		request.Headers.Add("Cookie", staffCookie);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.Should().NotContain("Requester request");
	}

	private async Task<JobRequestResult> SubmitAsync(AppUserId requesterId, RequestHoldingAreaId holdingAreaId, string description) =>
		await seedClient.Requests.SubmitAsync(new() {
			Context = new() { Actor = requesterId, CorrelationId = Guid.NewGuid() },
			HoldingAreaId = holdingAreaId,
			Description = description,
		});

	private async Task<HttpResponseMessage> GetDetailPageAsync(long jobNodeId, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/Requests/{jobNodeId}");
		request.Headers.Add("Cookie", authCookie);
		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetDetailPageFormAsync(long jobNodeId, string authCookie)
	{
		var response = await GetDetailPageAsync(jobNodeId, authCookie);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery")
								?? throw new InvalidOperationException("No antiforgery cookie in request detail page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in request detail page body.");

		return (ExtractCookiePair(antiforgeryCookie), token);
	}

	private async Task<HttpResponseMessage> PostAddNoteAsync(
		long jobNodeId, string authCookie, string antiforgeryCookie, string token, string content)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, $"/Requests/{jobNodeId}?handler=AddNote");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["NoteInput.Content"] = content,
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostSubmitAsync(
		string authCookie, string antiforgeryCookie, string token, RequestHoldingAreaId holdingAreaId, string description)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Requests?handler=Submit");
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
			["Submit.Description"] = description,
			["Submit.HoldingAreaId"] = holdingAreaId.Value.ToString(CultureInfo.InvariantCulture),
			["__RequestVerificationToken"] = token,
		});

		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> GetPageAsync(string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/Requests");
		request.Headers.Add("Cookie", authCookie);
		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetPageFormAsync(string authCookie)
	{
		var response = await GetPageAsync(authCookie);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery")
								?? throw new InvalidOperationException("No antiforgery cookie in Requests page response.");
		var token = AntiforgeryTokenPattern().Match(body) is { Success: true } match
			? match.Groups["token"].Value
			: throw new InvalidOperationException("No antiforgery token in Requests page body.");

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
		var authCookie = FindSetCookie(response, "Identity.Application")
						 ?? throw new InvalidOperationException("Sign-in did not set the authentication cookie.");

		return ExtractCookiePair(authCookie);
	}

	private async Task<(string CookieHeader, string Token)> GetLoginFormAsync()
	{
		var response = await client.GetAsync("/Account/Login");
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery")
								?? throw new InvalidOperationException("No antiforgery cookie in login page response.");
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

	private async Task<RequestHoldingAreaId> SeedHoldingAreaAsync()
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();

		await using var insertNode = connection.CreateCommand();
		insertNode.CommandText = """
								 INSERT INTO job_node (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
								 VALUES ($parentId, 'Holding area', $ownerId, $ownerId, $priorityId, $postedAt);
								 SELECT last_insert_rowid();
								 """;
		_ = insertNode.Parameters.AddWithValue("$parentId", rootId.Value);
		_ = insertNode.Parameters.AddWithValue("$ownerId", await ReadRootOwnerIdAsync(connection));
		_ = insertNode.Parameters.AddWithValue("$priorityId", PriorityMedium);
		_ = insertNode.Parameters.AddWithValue("$postedAt", DateTimeOffset.UtcNow.UtcTicks - DateTime.UnixEpoch.Ticks);
		var jobNodeId = (long)(await insertNode.ExecuteScalarAsync())!;

		await using var insertHoldingArea = connection.CreateCommand();
		insertHoldingArea.CommandText = """
										INSERT INTO request_holding_area (job_node_id, name, default_priority_id, is_active)
										VALUES ($jobNodeId, 'IT Intake', $priorityId, 1);
										SELECT last_insert_rowid();
										""";
		_ = insertHoldingArea.Parameters.AddWithValue("$jobNodeId", jobNodeId);
		_ = insertHoldingArea.Parameters.AddWithValue("$priorityId", PriorityMedium);
		var holdingAreaId = (long)(await insertHoldingArea.ExecuteScalarAsync())!;

		return new(holdingAreaId);
	}

	private static async Task<long> ReadRootOwnerIdAsync(SqliteConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT owner_user_id FROM job_node WHERE parent_id IS NULL;";
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
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
