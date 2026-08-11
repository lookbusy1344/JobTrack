namespace JobTrack.Web.IntegrationTests;

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
///     Direct-HTTP tests for the external HTTP API's leaf-completion and reopen-and-start composites
///     (ADR 0045, unified-leaf-workflow plan Stage 3): <c>POST /jobs/{nodeId}/complete</c> and
///     <c>POST /jobs/{nodeId}/reopen-and-start-session</c>.
/// </summary>
public sealed partial class LeafCompletionAndReopenApiTests : IAsyncLifetime, IDisposable
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
			UserName = "admin.leaf-completion-tests",
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
	public async Task A_controlling_worker_can_complete_a_leaf_with_one_active_session_via_the_api()
	{
		var workerId = await SeedEmployeeAsync("complete.worker");
		var leafId = await AddChildAsync(rootId, workerId, "Fit cabinets");
		var authCookie = await SignInAsync("complete.worker");
		var (antiforgeryCookie, antiforgeryToken) = await GetAntiforgeryTokenAsync(authCookie);
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});

		var response = await PostJsonAsync(
			$"/api/jobs/{leafId.Value}/complete",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""
			  { "version": 2, "expectedActiveSessions": [{ "id": {{session.Id.Value}}, "version": {{session.Version}} }] }
			  """);
		var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		body.RootElement.GetProperty("achievement").GetString().Should().Be("Success");
		body.RootElement.GetProperty("finishedSessions").GetArrayLength().Should().Be(1);
	}

	[Fact]
	public async Task Completing_with_a_stale_expected_session_set_is_rejected_as_a_conflict()
	{
		var workerId = await SeedEmployeeAsync("complete.stale");
		var leafId = await AddChildAsync(rootId, workerId, "Fit cabinets");
		var authCookie = await SignInAsync("complete.stale");
		var (antiforgeryCookie, antiforgeryToken) = await GetAntiforgeryTokenAsync(authCookie);
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});

		var response = await PostJsonAsync(
			$"/api/jobs/{leafId.Value}/complete",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""
			  { "version": 2, "expectedActiveSessions": [{ "id": {{session.Id.Value}}, "version": {{session.Version + 1}} }] }
			  """);

		response.StatusCode.Should().Be(HttpStatusCode.Conflict);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task A_non_controlling_worker_cannot_complete_a_leaf_via_the_api()
	{
		var workerId = await SeedEmployeeAsync("complete.owner");
		var otherWorkerId = await SeedEmployeeAsync("complete.stranger");
		var leafId = await AddChildAsync(rootId, workerId, "Fit cabinets");
		var authCookie = await SignInAsync("complete.stranger");
		var (antiforgeryCookie, antiforgeryToken) = await GetAntiforgeryTokenAsync(authCookie);
		var session = await seedClient.Work.StartWorkAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leafId,
			WorkedByUserId = workerId,
		});

		var response = await PostJsonAsync(
			$"/api/jobs/{leafId.Value}/complete",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""
			  { "version": 2, "expectedActiveSessions": [{ "id": {{session.Id.Value}}, "version": {{session.Version}} }] }
			  """);

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task A_prior_participant_can_reopen_and_start_for_themselves_via_the_api()
	{
		var workerId = await SeedEmployeeAsync("reopen.participant");
		var otherWorkerId = await SeedEmployeeAsync("reopen.new-owner");
		var leafId = await CreateTerminalLeafAsync(workerId);
		await ReassignOwnerAsync(leafId, otherWorkerId);
		var authCookie = await SignInAsync("reopen.participant");
		var (antiforgeryCookie, antiforgeryToken) = await GetAntiforgeryTokenAsync(authCookie);

		var response = await PostJsonAsync(
			$"/api/jobs/{leafId.Value}/reopen-and-start-session",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""{ "version": 3, "reason": "Work resumed", "workedByUserId": {{workerId.Value}} }""");
		var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.Created);
		body.RootElement.GetProperty("achievement").GetString().Should().Be("InProgress");
		body.RootElement.GetProperty("session").GetProperty("workedByUserId").GetInt64().Should().Be(workerId.Value);
	}

	[Fact]
	public async Task A_prior_participant_cannot_start_for_a_different_worker_via_the_api()
	{
		var workerId = await SeedEmployeeAsync("reopen.participant-forbidden");
		var otherWorkerId = await SeedEmployeeAsync("reopen.new-owner-forbidden");
		var leafId = await CreateTerminalLeafAsync(workerId);
		await ReassignOwnerAsync(leafId, otherWorkerId);
		var authCookie = await SignInAsync("reopen.participant-forbidden");
		var (antiforgeryCookie, antiforgeryToken) = await GetAntiforgeryTokenAsync(authCookie);

		var response = await PostJsonAsync(
			$"/api/jobs/{leafId.Value}/reopen-and-start-session",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""{ "version": 3, "reason": "Trying to hand it off", "workedByUserId": {{otherWorkerId.Value}} }""");

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Reopening_an_archived_leaf_is_rejected_as_a_conflict_via_the_api()
	{
		var workerId = await SeedEmployeeAsync("reopen.archived");
		var leafId = await CreateTerminalLeafAsync(workerId);
		var node = await seedClient.Query.GetJobNodeAsync(new() { Context = ContextFor(administratorId), NodeId = leafId });
		_ = await seedClient.Jobs.ArchiveAsync(new() { Context = ContextFor(administratorId), NodeId = leafId, Version = node.Node.Version });
		var authCookie = await SignInAsync("reopen.archived");
		var (antiforgeryCookie, antiforgeryToken) = await GetAntiforgeryTokenAsync(authCookie);

		var response = await PostJsonAsync(
			$"/api/jobs/{leafId.Value}/reopen-and-start-session",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""{ "version": 3, "reason": "Trying anyway", "workedByUserId": {{workerId.Value}} }""");

		response.StatusCode.Should().Be(HttpStatusCode.Conflict);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task Reopening_with_a_blank_reason_is_rejected_as_a_bad_request_via_the_api()
	{
		var workerId = await SeedEmployeeAsync("reopen.blank-reason");
		var leafId = await CreateTerminalLeafAsync(workerId);
		var authCookie = await SignInAsync("reopen.blank-reason");
		var (antiforgeryCookie, antiforgeryToken) = await GetAntiforgeryTokenAsync(authCookie);

		var response = await PostJsonAsync(
			$"/api/jobs/{leafId.Value}/reopen-and-start-session",
			authCookie,
			antiforgeryCookie,
			antiforgeryToken,
			$$"""{ "version": 3, "reason": " ", "workedByUserId": {{workerId.Value}} }""");

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

		var leafWork = await seedClient.Query.GetLeafWorkAsync(
			new() { Context = ContextFor(administratorId), JobNodeId = leafId });
		leafWork.Achievement.Should().Be(Achievement.Unsuccessful);
		leafWork.Version.Should().Be(3);
	}

	[Fact]
	public async Task A_worker_can_reopen_and_start_via_a_bearer_token_without_an_antiforgery_token()
	{
		var workerId = await SeedEmployeeAsync("reopen.bearer");
		var leafId = await CreateTerminalLeafAsync(workerId);
		var issued = await seedClient.Tokens.IssueAsync(new() {
			Context = ContextFor(workerId),
			TargetUserId = workerId,
			Label = "cli-test-token",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});

		var response = await PostJsonWithBearerAsync(
			$"/api/jobs/{leafId.Value}/reopen-and-start-session",
			issued.Token,
			$$"""{ "version": 3, "reason": "Work resumed", "workedByUserId": {{workerId.Value}} }""");

		response.StatusCode.Should().Be(HttpStatusCode.Created);
	}

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	private async Task<JobNodeId> CreateTerminalLeafAsync(AppUserId workerId)
	{
		var leafId = await AddChildAsync(rootId, workerId, "Terminal leaf");
		var session = await seedClient.Work.StartWorkAsync(
			new() { Context = ContextFor(workerId), JobNodeId = leafId, WorkedByUserId = workerId });
		_ = await seedClient.Work.FinishSessionAsync(
			new() { Context = ContextFor(workerId), SessionId = session.Id, Version = session.Version });
		_ = await seedClient.Work.SetAchievementAsync(new() {
			Context = ContextFor(administratorId),
			JobNodeId = leafId,
			NewAchievement = Achievement.Unsuccessful,
			Reason = "Did not work out",
			Version = 2,
		});

		return leafId;
	}

	private async Task ReassignOwnerAsync(JobNodeId leafId, AppUserId newOwnerId)
	{
		_ = await seedClient.Jobs.EditAsync(new() {
			Context = ContextFor(administratorId),
			NodeId = leafId,
			Description = "Reassigned away from the original worker",
			OwnerUserId = newOwnerId,
			Priority = Priority.Medium,
			Version = 1,
		});
	}

	private async Task<JobNodeId> AddChildAsync(JobNodeId parentId, AppUserId ownerId, string description)
	{
		var result = await seedClient.Jobs.AddChildAsync(new() {
			Context = ContextFor(administratorId),
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		});
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() { Context = ContextFor(administratorId), JobNodeId = result.Id });

		return result.Id;
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
