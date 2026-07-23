namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using AwesomeAssertions;
using Database;
using Domain.Schedules;
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
///     Direct-HTTP tests for the external HTTP API's cost-report surface (plan §4.3 slice 5, ADR
///     0030): <c>GET /api/jobs/{nodeId}/cost</c> and <c>/cost/hierarchy</c>. Cost visibility is never
///     an unqualified baseline capability (spec §7.3) — every test signs in as an ordinary worker for
///     the denial case and a cost viewer for the authorized cases.
/// </summary>
public sealed partial class CostReportApiTests : IAsyncLifetime, IDisposable
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
		var bootstrap = await seedClient.Installation.BootstrapAdministratorAsync(new() {
			DisplayName = "Bootstrap Administrator",
			IanaTimeZone = "Etc/UTC",
			UserName = "admin.cost-report-tests",
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
	public async Task A_cost_viewer_can_get_a_leafs_cost_details_via_the_api()
	{
		var (workerId, leafId) = await SeedWorkedLeafWithFinishedSessionAsync("cost.details.worker");
		_ = await SeedEmployeeAsync("cost.details.viewer", EmployeeRole.CostViewer);
		var authCookie = await SignInAsync("cost.details.viewer");

		var response = await GetAsync($"/api/jobs/{leafId.Value}/cost?asOf=2026-01-02T00:00:00%2B00:00", authCookie);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("nodeId").GetInt64().Should().Be(leafId.Value);
		jsonDocument.RootElement.GetProperty("displayedCost").GetDecimal().Should().BeGreaterThan(0m);
		jsonDocument.RootElement.GetProperty("trace").GetArrayLength().Should().BeGreaterThan(0);
		jsonDocument.RootElement.GetProperty("tzdbVersion").GetString().Should().Be(DateTimeZoneProviders.Tzdb.VersionId);
	}

	[Fact]
	public async Task A_cost_viewer_can_get_hierarchy_totals_via_the_api()
	{
		var (workerId, leafId) = await SeedWorkedLeafWithFinishedSessionAsync("cost.hierarchy.worker");
		_ = await SeedEmployeeAsync("cost.hierarchy.viewer", EmployeeRole.CostViewer);
		var authCookie = await SignInAsync("cost.hierarchy.viewer");

		var response = await GetAsync($"/api/jobs/{rootId.Value}/cost/hierarchy?asOf=2026-01-02T00:00:00%2B00:00", authCookie);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("nodeId").GetInt64().Should().Be(rootId.Value);
		var nodes = jsonDocument.RootElement.GetProperty("nodes");
		nodes.EnumerateArray().Should().Contain(node => node.GetProperty("nodeId").GetInt64() == leafId.Value);
		jsonDocument.RootElement.GetProperty("tzdbVersion").GetString().Should().Be(DateTimeZoneProviders.Tzdb.VersionId);
	}

	[Fact]
	public async Task A_worker_without_cost_permission_is_denied_and_receives_problem_details()
	{
		var (workerId, leafId) = await SeedWorkedLeafWithFinishedSessionAsync("cost.denied.worker");
		var authCookie = await SignInAsync("cost.denied.worker");

		var response = await GetAsync($"/api/jobs/{leafId.Value}/cost?asOf=2026-01-02T00:00:00%2B00:00", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task A_worker_without_cost_permission_is_denied_via_a_bearer_token_without_leaking_rate_or_cost_data()
	{
		var (workerId, leafId) = await SeedWorkedLeafWithFinishedSessionAsync("cost.bearer-denied.worker");
		var issued = await seedClient.Tokens.IssueAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			TargetUserId = workerId,
			Label = "cli-test-token",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});

		var response = await GetWithBearerAsync($"/api/jobs/{leafId.Value}/cost?asOf=2026-01-02T00:00:00%2B00:00", issued.Token);
		var body = await response.Content.ReadAsStringAsync();

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		// The seeded worker's own known rate (25/hr) and resulting exact cost (200.00 for an 8-hour
		// session) must never surface in a denied response -- sensitive-data-denial evidence (ADR
		// 0029, remediation plan §3.4), not just a bare 403.
		body.Should().NotContain("25.0");
		body.Should().NotContain("200.0");
		body.Should().NotContain(workerId.Value.ToString(CultureInfo.InvariantCulture));
	}

	[Fact]
	public async Task A_cost_viewer_can_get_cost_details_via_a_bearer_token()
	{
		var (workerId, leafId) = await SeedWorkedLeafWithFinishedSessionAsync("cost.bearer.worker");
		var viewerId = await SeedEmployeeAsync("cost.bearer.viewer", EmployeeRole.CostViewer);
		var issued = await seedClient.Tokens.IssueAsync(new() {
			Context = new() { Actor = viewerId, CorrelationId = Guid.NewGuid() },
			TargetUserId = viewerId,
			Label = "cli-test-token",
			ExpiresAt = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(1),
		});

		var response = await GetWithBearerAsync($"/api/jobs/{leafId.Value}/cost?asOf=2026-01-02T00:00:00%2B00:00", issued.Token);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Cost_details_rejects_a_non_positive_trace_segment_limit()
	{
		var (_, leafId) = await SeedWorkedLeafWithFinishedSessionAsync("cost.trace-limit.worker");
		_ = await SeedEmployeeAsync("cost.trace-limit.viewer", EmployeeRole.CostViewer);
		var authCookie = await SignInAsync("cost.trace-limit.viewer");

		var response = await GetAsync(
			$"/api/jobs/{leafId.Value}/cost?asOf=2026-01-02T00:00:00%2B00:00&maxTraceSegments=0",
			authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task Cost_details_reports_an_unprocessable_entity_when_no_rate_resolves()
	{
		var (_, leafId) = await SeedWorkedLeafWithFinishedSessionAsync("cost.no-rate.worker", addUserRate: false);
		_ = await SeedEmployeeAsync("cost.no-rate.viewer", EmployeeRole.CostViewer);
		var authCookie = await SignInAsync("cost.no-rate.viewer");

		var response = await GetAsync($"/api/jobs/{leafId.Value}/cost?asOf=2026-01-02T00:00:00%2B00:00", authCookie);

		// A valid, authorized request the server cannot cost because no rate source applies is a
		// semantic failure of the request against server data, not a caller usage error (spec
		// jobtrack_spec_claude §12.6: MissingRateException -> 422).
		response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task Hierarchy_totals_rejects_a_subtree_larger_than_the_requested_node_limit()
	{
		_ = await SeedWorkedLeafWithFinishedSessionAsync("cost.node-limit.worker");
		_ = await SeedEmployeeAsync("cost.node-limit.viewer", EmployeeRole.CostViewer);
		var authCookie = await SignInAsync("cost.node-limit.viewer");

		var response = await GetAsync(
			$"/api/jobs/{rootId.Value}/cost/hierarchy?asOf=2026-01-02T00:00:00%2B00:00&maxHierarchyNodes=1",
			authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	private async Task<(AppUserId WorkerId, JobNodeId LeafId)> SeedWorkedLeafWithFinishedSessionAsync(
		string workerUserName, bool addUserRate = true)
	{
		var workerId = await SeedEmployeeAsync(workerUserName, EmployeeRole.Worker);
		var leaf = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			ParentId = rootId,
			Description = "Fit cabinets",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			JobNodeId = leaf.Id,
		});
		_ = await seedClient.Schedules.AddScheduleExceptionAsync(new() {
			Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
			UserId = workerId,
			Entry = new(
				ScheduleExceptionEffect.AddWorkingTime,
				new(Instant.FromUtc(2026, 1, 1, 9, 0), Instant.FromUtc(2026, 1, 1, 18, 0)),
				null),
			Reason = "Full working window for cost-report API tests",
		});
		if (addUserRate) {
			_ = await seedClient.Rates.AddUserCostRateAsync(new() {
				Context = new() { Actor = administratorId, CorrelationId = Guid.NewGuid() },
				UserId = workerId,
				Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
			});
		}

		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			LeafWorkId = leaf.Id,
			WorkedByUserId = workerId,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
		});
		_ = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() { Actor = workerId, CorrelationId = Guid.NewGuid() },
			SessionId = started.Id,
			Version = started.Version,
			FinishedAt = Instant.FromUtc(2026, 1, 1, 17, 0),
		});

		return (workerId, leaf.Id);
	}

	private async Task<HttpResponseMessage> GetAsync(string path, string authCookie)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Add("Cookie", authCookie);
		return await client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> GetWithBearerAsync(string path, string token)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Authorization = new("Bearer", token);
		return await client.SendAsync(request);
	}

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
