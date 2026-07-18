namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
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
using NodaTime;
using Persistence.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     Direct-HTTP tests for the requester intake external API surface (ADR 0033, plan §9 Stage 7):
///     <c>GET /api/request-holding-areas</c>, <c>POST /api/requests</c>, and <c>GET /api/requests</c>.
///     Reachable via either the cookie scheme or a bearer PAT identically (ADR 0029), and denied to
///     every role except <see cref="EmployeeRole.Requester" />.
/// </summary>
public sealed partial class RequestsApiTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AntiforgeryHeaderName = "X-CSRF-TOKEN";
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
		var bootstrap = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.requests-api-tests",
			Password = "Bootstrap-Horse-Battery-77!",
			CorrelationId = Guid.NewGuid(),
		});
		rootId = bootstrap.RootJobNodeId;

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
	public async Task A_requester_can_list_eligible_holding_areas_submit_and_list_own_requests_via_the_api()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		_ = await SeedEmployeeAsync("api.requester.happy", EmployeeRole.Requester);
		var authCookie = await SignInAsync("api.requester.happy");

		var holdingAreasResponse = await GetAsync("/api/request-holding-areas", authCookie);
		var holdingAreasJson = JsonDocument.Parse(await holdingAreasResponse.Content.ReadAsStringAsync());
		holdingAreasResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		holdingAreasJson.RootElement.GetArrayLength().Should().Be(1);

		var submitResponse = await PostJsonAsync(
			"/api/requests", authCookie,
			$$"""{"description":"Printer will not turn on","holdingAreaId":{{holdingAreaId.Value}}}""");
		submitResponse.StatusCode.Should().Be(HttpStatusCode.Created);

		var listResponse = await GetAsync("/api/requests", authCookie);
		var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
		listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		listJson.RootElement.GetArrayLength().Should().Be(1);
		listJson.RootElement[0].GetProperty("description").GetString().Should().Be("Printer will not turn on");
	}

	[Fact]
	public async Task A_requester_can_submit_a_request_via_a_bearer_token_without_an_antiforgery_token()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("api.requester.bearer", EmployeeRole.Requester);
		var token = await IssueTokenAsync(requesterId);

		using var request = new HttpRequestMessage(HttpMethod.Post, "/api/requests");
		request.Headers.Authorization = new("Bearer", token);
		request.Content = new StringContent(
			$$"""{"description":"Printer will not turn on","holdingAreaId":{{holdingAreaId.Value}}}""",
			Encoding.UTF8, "application/json");
		var response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.Created);
	}

	[Fact]
	public async Task A_requester_cannot_call_the_operational_job_root_endpoint()
	{
		_ = await SeedEmployeeAsync("api.requester.blocked", EmployeeRole.Requester);
		var authCookie = await SignInAsync("api.requester.blocked");

		var response = await GetAsync("/api/jobs/root", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task A_worker_cannot_call_the_requests_endpoints()
	{
		_ = await SeedEmployeeAsync("api.worker.blocked", EmployeeRole.Worker);
		var authCookie = await SignInAsync("api.worker.blocked");

		var response = await GetAsync("/api/requests", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task Submitting_into_an_inactive_holding_area_returns_a_forbidden_problem_response()
	{
		var holdingAreaId = await SeedHoldingAreaAsync(false);
		_ = await SeedEmployeeAsync("api.requester.inactive", EmployeeRole.Requester);
		var authCookie = await SignInAsync("api.requester.inactive");

		var response = await PostJsonAsync(
			"/api/requests", authCookie, $$"""{"description":"Printer will not turn on","holdingAreaId":{{holdingAreaId.Value}}}""");

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task Submitting_into_a_nonexistent_holding_area_returns_a_not_found_problem_response()
	{
		_ = await SeedEmployeeAsync("api.requester.notfound", EmployeeRole.Requester);
		var authCookie = await SignInAsync("api.requester.notfound");

		var response = await PostJsonAsync(
			"/api/requests", authCookie, """{"description":"Printer will not turn on","holdingAreaId":999999}""");

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task Submitting_a_request_with_a_blank_description_returns_a_bad_request_problem_response()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		_ = await SeedEmployeeAsync("api.requester.blank-submit", EmployeeRole.Requester);
		var authCookie = await SignInAsync("api.requester.blank-submit");

		var response = await PostJsonAsync(
			"/api/requests", authCookie, $$"""{"description":"   ","holdingAreaId":{{holdingAreaId.Value}}}""");

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task Extra_fields_in_the_submit_body_have_no_effect_beyond_the_allow_listed_fields()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("api.requester.mass-assignment", EmployeeRole.Requester);
		var authCookie = await SignInAsync("api.requester.mass-assignment");

		var response = await PostJsonAsync(
			"/api/requests", authCookie,
			$$"""
			  {
			    "description":"Printer will not turn on",
			    "holdingAreaId":{{holdingAreaId.Value}},
			    "ownerUserId":{{requesterId.Value}},
			    "parentId":{{rootId.Value}},
			    "kind":"Leaf",
			    "priority":"Urgent"
			  }
			  """);
		response.StatusCode.Should().Be(HttpStatusCode.Created);
		var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		var jobNodeId = body.RootElement.GetProperty("jobNodeId").GetInt64();

		var (ownerUserId, parentId) = await ReadNodeOwnerAndParentAsync(jobNodeId);
		ownerUserId.Should().BeNull("the holding area's own configuration, not the caller, determines the default owner");
		parentId.Should().NotBe(rootId.Value, "the request's parent must be the holding area's own job node, not a caller-supplied parentId");
	}

	[Fact]
	public async Task A_requester_can_view_their_own_request_detail_via_the_api()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("api.requester.detail", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId);
		var authCookie = await SignInAsync("api.requester.detail");

		var response = await GetAsync($"/api/requests/{submitted.JobNodeId.Value}", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		body.RootElement.GetProperty("status").GetString().Should().Be("Submitted");
		body.RootElement.GetProperty("subtree").GetArrayLength().Should().Be(1);
	}

	[Fact]
	public async Task A_different_requester_cannot_view_someone_elses_request_via_the_api()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("api.requester.owner", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId);
		_ = await SeedEmployeeAsync("api.requester.stranger", EmployeeRole.Requester);
		var authCookie = await SignInAsync("api.requester.stranger");

		var response = await GetAsync($"/api/requests/{submitted.JobNodeId.Value}", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task Getting_a_nonexistent_request_returns_a_not_found_problem_response()
	{
		_ = await SeedEmployeeAsync("api.requester.missing", EmployeeRole.Requester);
		var authCookie = await SignInAsync("api.requester.missing");

		var response = await GetAsync("/api/requests/999999", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task A_job_manager_can_acknowledge_a_request_via_the_api()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("api.requester.for-ack", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId);
		_ = await SeedEmployeeAsync("api.jobmanager.ack", EmployeeRole.JobManager);
		var authCookie = await SignInAsync("api.jobmanager.ack");

		var response = await PostJsonAsync(
			$"/api/requests/{submitted.JobNodeId.Value}/acknowledge", authCookie, $$"""{"version":{{submitted.Version}}}""");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		body.RootElement.GetProperty("acknowledgedAt").GetDateTimeOffset().Should().NotBe(default);
	}

	[Fact]
	public async Task A_requester_cannot_acknowledge_their_own_request_via_the_api()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("api.requester.self-ack", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId);
		var authCookie = await SignInAsync("api.requester.self-ack");

		var response = await PostJsonAsync(
			$"/api/requests/{submitted.JobNodeId.Value}/acknowledge", authCookie, $$"""{"version":{{submitted.Version}}}""");

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Staff_and_the_requester_can_add_notes_with_the_expected_visibility_via_the_api()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("api.requester.notes", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId);
		_ = await SeedEmployeeAsync("api.jobmanager.notes", EmployeeRole.JobManager);
		var staffCookie = await SignInAsync("api.jobmanager.notes");
		var requesterCookie = await SignInAsync("api.requester.notes");

		var staffNoteResponse = await PostJsonAsync(
			$"/api/requests/{submitted.JobNodeId.Value}/comments", staffCookie,
			"""{"content":"Private triage note","visibleToRequester":false}""");
		staffNoteResponse.StatusCode.Should().Be(HttpStatusCode.Created);

		var requesterNoteResponse = await PostJsonAsync(
			$"/api/requests/{submitted.JobNodeId.Value}/comments", requesterCookie,
			"""{"content":"Any update?","visibleToRequester":true}""");
		requesterNoteResponse.StatusCode.Should().Be(HttpStatusCode.Created);

		var requesterView = await GetAsync($"/api/requests/{submitted.JobNodeId.Value}", requesterCookie);
		var requesterBody = JsonDocument.Parse(await requesterView.Content.ReadAsStringAsync());
		requesterBody.RootElement.GetProperty("notes").GetArrayLength().Should().Be(1);

		var staffView = await GetAsync($"/api/requests/{submitted.JobNodeId.Value}", staffCookie);
		var staffBody = JsonDocument.Parse(await staffView.Content.ReadAsStringAsync());
		staffBody.RootElement.GetProperty("notes").GetArrayLength().Should().Be(2);
	}

	[Fact]
	public async Task Adding_a_blank_request_note_returns_a_bad_request_problem_response()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await SeedEmployeeAsync("api.requester.blank-note", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId);
		var requesterCookie = await SignInAsync("api.requester.blank-note");

		var response = await PostJsonAsync(
			$"/api/requests/{submitted.JobNodeId.Value}/comments", requesterCookie,
			"""{"content":"   ","visibleToRequester":true}""");

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	private async Task<JobRequestResult> SubmitAsync(AppUserId requesterId, RequestHoldingAreaId holdingAreaId) =>
		await seedClient.Requests.SubmitAsync(new() {
			Context = new() { Actor = requesterId, CorrelationId = Guid.NewGuid() },
			HoldingAreaId = holdingAreaId,
			Description = "Printer will not turn on",
		});

	private async Task<(long? OwnerUserId, long ParentId)> ReadNodeOwnerAndParentAsync(long jobNodeId)
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT owner_user_id, parent_id FROM job_node WHERE id = $jobNodeId;";
		_ = command.Parameters.AddWithValue("$jobNodeId", jobNodeId);

		await using var reader = await command.ExecuteReaderAsync();
		_ = await reader.ReadAsync();
		return (reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.GetInt64(1));
	}

	private async Task<string> IssueTokenAsync(AppUserId userId)
	{
		var issued = await seedClient.Tokens.IssueAsync(new() {
			Context = new() { Actor = userId, CorrelationId = Guid.NewGuid() },
			TargetUserId = userId,
			Label = "requests-api-test-token",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});
		return issued.Token;
	}

	private async Task<HttpResponseMessage> GetAsync(string path, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", authCookie);
		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostJsonAsync(string path, string authCookie, string jsonBody)
	{
		var (antiforgeryCookie, antiforgeryToken) = await GetAntiforgeryTokenAsync(authCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, path);
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Headers.Add(AntiforgeryHeaderName, antiforgeryToken);
		request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetAntiforgeryTokenAsync(string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery-token");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery")
								?? throw new InvalidOperationException("No antiforgery cookie in token response.");
		var token = JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()
					?? throw new InvalidOperationException("No antiforgery token in token response.");

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

	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();

	private static string? FindSetCookie(HttpResponseMessage response, string nameContains) =>
		response.Headers.TryGetValues("Set-Cookie", out var values)
			? values.FirstOrDefault(value => value.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
			: null;

	private static string ExtractCookiePair(string setCookieHeader) => setCookieHeader.Split(';')[0];

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

	private async Task<RequestHoldingAreaId> SeedHoldingAreaAsync(bool isActive = true)
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
										VALUES ($jobNodeId, 'IT Intake', $priorityId, $isActive);
										SELECT last_insert_rowid();
										""";
		_ = insertHoldingArea.Parameters.AddWithValue("$jobNodeId", jobNodeId);
		_ = insertHoldingArea.Parameters.AddWithValue("$priorityId", PriorityMedium);
		_ = insertHoldingArea.Parameters.AddWithValue("$isActive", isActive ? 1 : 0);
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
