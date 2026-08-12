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
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

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
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.requester.happy", EmployeeRole.Requester);
		var authCookie = await client.SignInAsync("api.requester.happy");

		var holdingAreasResponse = await client.GetAuthenticatedAsync("/api/request-holding-areas", authCookie);
		var holdingAreasJson = JsonDocument.Parse(await holdingAreasResponse.Content.ReadAsStringAsync());
		holdingAreasResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		holdingAreasJson.RootElement.GetArrayLength().Should().Be(1);

		var submitResponse = await PostJsonAsync(
			"/api/requests", authCookie,
			$$"""{"description":"Printer will not turn on","holdingAreaId":{{holdingAreaId.Value}}}""");
		submitResponse.StatusCode.Should().Be(HttpStatusCode.Created);

		var listResponse = await client.GetAuthenticatedAsync("/api/requests", authCookie);
		var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
		listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		listJson.RootElement.GetArrayLength().Should().Be(1);
		listJson.RootElement[0].GetProperty("description").GetString().Should().Be("Printer will not turn on");
	}

	[Fact]
	public async Task A_requester_can_submit_a_request_via_a_bearer_token_without_an_antiforgery_token()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.requester.bearer", EmployeeRole.Requester);
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
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.requester.blocked", EmployeeRole.Requester);
		var authCookie = await client.SignInAsync("api.requester.blocked");

		var response = await client.GetAuthenticatedAsync("/api/jobs/root", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task A_worker_cannot_call_the_requests_endpoints()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.worker.blocked", EmployeeRole.Worker);
		var authCookie = await client.SignInAsync("api.worker.blocked");

		var response = await client.GetAuthenticatedAsync("/api/requests", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task Submitting_into_an_inactive_holding_area_returns_a_forbidden_problem_response()
	{
		var holdingAreaId = await SeedHoldingAreaAsync(false);
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.requester.inactive", EmployeeRole.Requester);
		var authCookie = await client.SignInAsync("api.requester.inactive");

		var response = await PostJsonAsync(
			"/api/requests", authCookie, $$"""{"description":"Printer will not turn on","holdingAreaId":{{holdingAreaId.Value}}}""");

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task Submitting_into_a_nonexistent_holding_area_returns_a_not_found_problem_response()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.requester.notfound", EmployeeRole.Requester);
		var authCookie = await client.SignInAsync("api.requester.notfound");

		var response = await PostJsonAsync(
			"/api/requests", authCookie, """{"description":"Printer will not turn on","holdingAreaId":999999}""");

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task Submitting_a_request_with_a_blank_description_returns_a_bad_request_problem_response()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.requester.blank-submit", EmployeeRole.Requester);
		var authCookie = await client.SignInAsync("api.requester.blank-submit");

		var response = await PostJsonAsync(
			"/api/requests", authCookie, $$"""{"description":"   ","holdingAreaId":{{holdingAreaId.Value}}}""");

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task Extra_fields_in_the_submit_body_have_no_effect_beyond_the_allow_listed_fields()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.requester.mass-assignment", EmployeeRole.Requester);
		var authCookie = await client.SignInAsync("api.requester.mass-assignment");

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
		var requesterId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.requester.detail", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId);
		var authCookie = await client.SignInAsync("api.requester.detail");

		var response = await client.GetAuthenticatedAsync($"/api/requests/{submitted.JobNodeId.Value}", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		body.RootElement.GetProperty("requesterUserId").GetInt64().Should().Be(requesterId.Value);
		body.RootElement.GetProperty("requesterDisplayName").GetString().Should().Be("api.requester.detail");
		body.RootElement.GetProperty("requesterUserName").GetString().Should().Be("api.requester.detail");
		body.RootElement.GetProperty("status").GetString().Should().Be("Submitted");
		body.RootElement.GetProperty("subtree").GetArrayLength().Should().Be(1);
		body.RootElement.GetProperty("subtree")[0].GetProperty("allocatedHours").GetDecimal().Should().Be(0m);
	}

	[Fact]
	public async Task A_different_requester_cannot_view_someone_elses_request_via_the_api()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.requester.owner", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId);
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.requester.stranger", EmployeeRole.Requester);
		var authCookie = await client.SignInAsync("api.requester.stranger");

		var response = await client.GetAuthenticatedAsync($"/api/requests/{submitted.JobNodeId.Value}", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task Getting_a_nonexistent_request_returns_a_not_found_problem_response()
	{
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.requester.missing", EmployeeRole.Requester);
		var authCookie = await client.SignInAsync("api.requester.missing");

		var response = await client.GetAuthenticatedAsync("/api/requests/999999", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task A_job_manager_can_acknowledge_a_request_via_the_api()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.requester.for-ack", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId);
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.jobmanager.ack", EmployeeRole.JobManager);
		var authCookie = await client.SignInAsync("api.jobmanager.ack");

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
		var requesterId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.requester.self-ack", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId);
		var authCookie = await client.SignInAsync("api.requester.self-ack");

		var response = await PostJsonAsync(
			$"/api/requests/{submitted.JobNodeId.Value}/acknowledge", authCookie, $$"""{"version":{{submitted.Version}}}""");

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Staff_and_the_requester_can_add_notes_with_the_expected_visibility_via_the_api()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.requester.notes", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId);
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.jobmanager.notes", EmployeeRole.JobManager);
		var staffCookie = await client.SignInAsync("api.jobmanager.notes");
		var requesterCookie = await client.SignInAsync("api.requester.notes");

		var staffNoteResponse = await PostJsonAsync(
			$"/api/requests/{submitted.JobNodeId.Value}/comments", staffCookie,
			"""{"content":"Private triage note","visibleToRequester":false}""");
		staffNoteResponse.StatusCode.Should().Be(HttpStatusCode.Created);

		var requesterNoteResponse = await PostJsonAsync(
			$"/api/requests/{submitted.JobNodeId.Value}/comments", requesterCookie,
			"""{"content":"Any update?","visibleToRequester":true}""");
		requesterNoteResponse.StatusCode.Should().Be(HttpStatusCode.Created);

		var requesterView = await client.GetAuthenticatedAsync($"/api/requests/{submitted.JobNodeId.Value}", requesterCookie);
		var requesterBody = JsonDocument.Parse(await requesterView.Content.ReadAsStringAsync());
		requesterBody.RootElement.GetProperty("notes").GetArrayLength().Should().Be(1);

		var staffView = await client.GetAuthenticatedAsync($"/api/requests/{submitted.JobNodeId.Value}", staffCookie);
		var staffBody = JsonDocument.Parse(await staffView.Content.ReadAsStringAsync());
		staffBody.RootElement.GetProperty("notes").GetArrayLength().Should().Be(2);
	}

	[Fact]
	public async Task Adding_a_blank_request_note_returns_a_bad_request_problem_response()
	{
		var holdingAreaId = await SeedHoldingAreaAsync();
		var requesterId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "api.requester.blank-note", EmployeeRole.Requester);
		var submitted = await SubmitAsync(requesterId, holdingAreaId);
		var requesterCookie = await client.SignInAsync("api.requester.blank-note");

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



	private async Task<HttpResponseMessage> PostJsonAsync(string path, string authCookie, string jsonBody)
	{
		var (antiforgeryCookie, antiforgeryToken) = await client.GetAntiforgeryTokenAsync(authCookie);

		using var request = new HttpRequestMessage(HttpMethod.Post, path);
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Headers.Add(AntiforgeryHeaderName, antiforgeryToken);
		request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
		return await client.SendAsync(request);
	}







	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();



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



}
