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
///     Direct-HTTP tests for the external HTTP API's work-session surface (plan §4.3 slice 2, ADR
///     0030): start (also the "resume" UI action), finish (also "pause"/"stop"), correct, and list a
///     worker's sessions on a leaf.
/// </summary>
public sealed partial class WorkSessionApiTests : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string KnownPassword = "Correct-Horse-Battery-42!";
	private const string AdministratorPassword = "Bootstrap-Horse-Battery-77!";
	private const string AntiforgeryHeaderName = "X-CSRF-TOKEN";

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
		var bootstrap = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.work-session-tests",
			Password = AdministratorPassword,
			CorrelationId = Guid.NewGuid(),
		});
		administratorId = bootstrap.AdministratorId;
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
	public async Task A_worker_can_start_and_then_list_their_own_session_via_the_api()
	{
		var workerId = await SeedEmployeeAsync("sessions.start.worker");
		var leafId = await AddChildAsync(rootId, workerId, "Fit cabinets");
		var authCookie = await SignInAsync("sessions.start.worker");
		var (antiforgeryCookie, antiforgeryToken) = await GetAntiforgeryTokenAsync(authCookie);

		var startResponse = await PostJsonAsync(
			$"/api/jobs/{leafId.Value}/sessions",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""{ "workedByUserId": {{workerId.Value}} }""");
		var startedSession = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());

		startResponse.StatusCode.Should().Be(HttpStatusCode.Created);
		startedSession.RootElement.GetProperty("leafWorkId").GetInt64().Should().Be(leafId.Value);
		startedSession.RootElement.GetProperty("finishedAt").ValueKind.Should().Be(JsonValueKind.Null);

		var listResponse = await GetAsync($"/api/jobs/{leafId.Value}/sessions?workedByUserId={workerId.Value}", authCookie);
		var listBody = await listResponse.Content.ReadAsStringAsync();

		listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		listBody.Should().Contain($"\"leafWorkId\":{leafId.Value}");
	}

	[Fact]
	public async Task Listing_sessions_bounds_results_by_offset_and_pageSize()
	{
		var workerId = await SeedEmployeeAsync("sessions.paging.worker");
		var leafId = await AddChildAsync(rootId, workerId, "Fit cabinets");
		var authCookie = await SignInAsync("sessions.paging.worker");
		await StartAndFinishSessionAsync(workerId, leafId);
		await StartAndFinishSessionAsync(workerId, leafId);

		var response = await GetAsync($"/api/jobs/{leafId.Value}/sessions?workedByUserId={workerId.Value}&pageSize=1", authCookie);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("items").GetArrayLength().Should().Be(1);
		jsonDocument.RootElement.GetProperty("pageSize").GetInt32().Should().Be(1);
		jsonDocument.RootElement.GetProperty("hasMore").GetBoolean().Should().BeTrue();
	}

	private async Task StartAndFinishSessionAsync(AppUserId workerId, JobNodeId leafId)
	{
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});
		_ = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			SessionId = started.Id,
			Version = started.Version,
		});
	}

	[Fact]
	public async Task Starting_a_second_session_for_an_already_active_worker_and_leaf_is_rejected_as_a_conflict()
	{
		var workerId = await SeedEmployeeAsync("sessions.duplicate.worker");
		var leafId = await AddChildAsync(rootId, workerId, "Fit cabinets");
		var authCookie = await SignInAsync("sessions.duplicate.worker");
		var (antiforgeryCookie, antiforgeryToken) = await GetAntiforgeryTokenAsync(authCookie);
		var body = $$"""{ "workedByUserId": {{workerId.Value}} }""";

		var firstResponse = await PostJsonAsync($"/api/jobs/{leafId.Value}/sessions", authCookie, antiforgeryCookie, antiforgeryToken, body);
		var retryResponse = await PostJsonAsync($"/api/jobs/{leafId.Value}/sessions", authCookie, antiforgeryCookie, antiforgeryToken, body);

		firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);
		retryResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
	}

	[Fact]
	public async Task A_worker_can_finish_their_active_session_via_the_api()
	{
		var workerId = await SeedEmployeeAsync("sessions.finish.worker");
		var leafId = await AddChildAsync(rootId, workerId, "Fit cabinets");
		var authCookie = await SignInAsync("sessions.finish.worker");
		var (antiforgeryCookie, antiforgeryToken) = await GetAntiforgeryTokenAsync(authCookie);
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});

		var response = await PostJsonAsync(
			$"/api/jobs/{leafId.Value}/sessions/{started.Id.Value}/finish",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""{ "version": {{started.Version}} }""");
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("finishedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
	}

	[Fact]
	public async Task Finishing_a_session_under_the_wrong_nodeId_is_rejected_as_not_found()
	{
		// Remediation plan §3.5: the route's parent identifier (nodeId) must actually match the
		// session's leaf, or the mismatch is a 404, not a mutation applied under a mismatched route.
		var workerId = await SeedEmployeeAsync("sessions.finish.wrong-node");
		var leafId = await AddChildAsync(rootId, workerId, "Fit cabinets");
		var otherLeafId = await AddChildAsync(rootId, workerId, "Frame walls");
		var authCookie = await SignInAsync("sessions.finish.wrong-node");
		var (antiforgeryCookie, antiforgeryToken) = await GetAntiforgeryTokenAsync(authCookie);
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});

		var response = await PostJsonAsync(
			$"/api/jobs/{otherLeafId.Value}/sessions/{started.Id.Value}/finish",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""{ "version": {{started.Version}} }""");

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task Retrying_a_finish_with_the_now_stale_version_is_rejected_as_a_conflict_not_reapplied()
	{
		var workerId = await SeedEmployeeAsync("sessions.finish.retry");
		var leafId = await AddChildAsync(rootId, workerId, "Fit cabinets");
		var authCookie = await SignInAsync("sessions.finish.retry");
		var (antiforgeryCookie, antiforgeryToken) = await GetAntiforgeryTokenAsync(authCookie);
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
		});
		var requestBody = $$"""{ "version": {{started.Version}} }""";

		var firstResponse = await PostJsonAsync(
			$"/api/jobs/{leafId.Value}/sessions/{started.Id.Value}/finish", authCookie, antiforgeryCookie, antiforgeryToken, requestBody);
		var retryResponse = await PostJsonAsync(
			$"/api/jobs/{leafId.Value}/sessions/{started.Id.Value}/finish", authCookie, antiforgeryCookie, antiforgeryToken, requestBody);

		firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		retryResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
	}

	[Fact]
	public async Task A_worker_can_correct_a_finished_sessions_interval_with_a_reason()
	{
		var workerId = await SeedEmployeeAsync("sessions.correct.worker");
		var leafId = await AddChildAsync(rootId, workerId, "Fit cabinets");
		var authCookie = await SignInAsync("sessions.correct.worker");
		var (antiforgeryCookie, antiforgeryToken) = await GetAntiforgeryTokenAsync(authCookie);
		var start = Instant.FromUtc(2026, 1, 1, 9, 0);
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
			StartedAt = start,
		});

		var response = await PostJsonAsync(
			$"/api/jobs/{leafId.Value}/sessions/{started.Id.Value}/correct",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""
			  {
			    "startedAt": "2026-01-01T08:30:00+00:00",
			    "finishedAt": "2026-01-01T12:00:00+00:00",
			    "reason": "Forgot to clock in on time",
			    "version": {{started.Version}}
			  }
			  """);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("startedAt").GetDateTimeOffset().Should().Be(
			DateTimeOffset.Parse("2026-01-01T08:30:00+00:00", CultureInfo.InvariantCulture));
	}

	[Fact]
	public async Task Correcting_a_session_under_the_wrong_nodeId_is_rejected_as_not_found()
	{
		var workerId = await SeedEmployeeAsync("sessions.correct.wrong-node");
		var leafId = await AddChildAsync(rootId, workerId, "Fit cabinets");
		var otherLeafId = await AddChildAsync(rootId, workerId, "Frame walls");
		var authCookie = await SignInAsync("sessions.correct.wrong-node");
		var (antiforgeryCookie, antiforgeryToken) = await GetAntiforgeryTokenAsync(authCookie);
		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leafId,
			WorkedByUserId = workerId,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
		});

		var response = await PostJsonAsync(
			$"/api/jobs/{otherLeafId.Value}/sessions/{started.Id.Value}/correct",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""
			  {
			    "startedAt": "2026-01-01T08:30:00+00:00",
			    "finishedAt": "2026-01-01T12:00:00+00:00",
			    "reason": "Attempting correction under the wrong node",
			    "version": {{started.Version}}
			  }
			  """);

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	// ADR 0041: recorded work is job data, viewable by every employee role (spec §7.3), so reading
	// another worker's sessions is allowed over both auth schemes. Editing remains gated by
	// CanManage's node-control rule, which this change does not touch.
	public async Task A_worker_can_view_another_workers_sessions()
	{
		var workerId = await SeedEmployeeAsync("sessions.foreign.worker");
		var otherWorkerId = await SeedEmployeeAsync("sessions.foreign.other");
		var leafId = await AddChildAsync(rootId, otherWorkerId, "Fit cabinets");
		var authCookie = await SignInAsync("sessions.foreign.worker");

		var response = await GetAsync($"/api/jobs/{leafId.Value}/sessions?workedByUserId={otherWorkerId.Value}", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		_ = workerId;
	}

	[Fact]
	public async Task A_worker_can_view_another_workers_sessions_via_a_bearer_token()
	{
		var workerId = await SeedEmployeeAsync("sessions.foreign.bearer-worker");
		var otherWorkerId = await SeedEmployeeAsync("sessions.foreign.bearer-other");
		var leafId = await AddChildAsync(rootId, otherWorkerId, "Fit cabinets");
		var issued = await seedClient.Tokens.IssueAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			TargetUserId = workerId,
			Label = "cli-test-token",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});

		var response = await GetWithBearerAsync($"/api/jobs/{leafId.Value}/sessions?workedByUserId={otherWorkerId.Value}", issued.Token);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task A_worker_can_start_a_session_via_a_bearer_token_without_an_antiforgery_token()
	{
		var workerId = await SeedEmployeeAsync("sessions.bearer.worker");
		var leafId = await AddChildAsync(rootId, workerId, "Fit cabinets");
		var issued = await seedClient.Tokens.IssueAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			TargetUserId = workerId,
			Label = "cli-test-token",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});

		var response = await PostJsonWithBearerAsync(
			$"/api/jobs/{leafId.Value}/sessions", issued.Token, $$"""{ "workedByUserId": {{workerId.Value}} }""");

		response.StatusCode.Should().Be(HttpStatusCode.Created);
	}

	private async Task<JobNodeId> AddChildAsync(JobNodeId parentId, AppUserId ownerId, string description)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = result.Id,
		});

		return result.Id;
	}

	private async Task<HttpResponseMessage> GetAsync(string path, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", authCookie);
		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostJsonAsync(
		string path, string authCookie, string antiforgeryCookie, string antiforgeryToken, string jsonBody)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, path);
		request.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
		request.Headers.Add(AntiforgeryHeaderName, antiforgeryToken);
		request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> GetWithBearerAsync(string path, string token)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Authorization = new("Bearer", token);
		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> PostJsonWithBearerAsync(string path, string token, string jsonBody)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, path);
		request.Headers.Authorization = new("Bearer", token);
		request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
		return await client.SendAsync(request);
	}

	private async Task<(string CookieHeader, string Token)> GetAntiforgeryTokenAsync(string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery-token");
		request.Headers.Add("Cookie", authCookie);
		var response = await client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		var antiforgeryCookie = FindSetCookie(response, "Antiforgery") ??
								throw new InvalidOperationException("No antiforgery cookie in token response.");
		var token = JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()
					?? throw new InvalidOperationException("No antiforgery token in token response.");

		return (ExtractCookiePair(antiforgeryCookie), token);
	}

	private async Task<AppUserId> SeedEmployeeAsync(string userName)
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
		_ = insertRole.Parameters.AddWithValue("$roleId", (short)EmployeeRole.Worker);
		_ = await insertRole.ExecuteNonQueryAsync();

		return new(appUserId);
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
