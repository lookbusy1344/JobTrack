namespace JobTrack.Web.IntegrationTests;

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Abstractions;
using Application;
using AwesomeAssertions;
using Domain.Schedules;
using NodaTime;
using Persistence.Sqlite;
using TestSupport;

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
		await SqliteSchemaTestSupport.DeployAsync(database.ConnectionString, ApplicationVersion, AppliedBy);

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
	public async Task A_cost_viewer_can_get_a_leafs_cost_details_via_the_api()
	{
		var (workerId, leafId) = await SeedWorkedLeafWithFinishedSessionAsync("cost.details.worker");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "cost.details.viewer", EmployeeRole.CostViewer);
		var authCookie = await client.SignInAsync("cost.details.viewer");

		var response = await client.GetAuthenticatedAsync($"/api/jobs/{leafId.Value}/cost?asOf=2026-01-02T00:00:00%2B00:00", authCookie);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("nodeId").GetInt64().Should().Be(leafId.Value);
		jsonDocument.RootElement.GetProperty("displayedCost").GetDecimal().Should().BeGreaterThan(0m);
		jsonDocument.RootElement.GetProperty("allocatedHours").GetDecimal().Should().Be(8m);
		jsonDocument.RootElement.GetProperty("trace").GetArrayLength().Should().BeGreaterThan(0);
		jsonDocument.RootElement.GetProperty("tzdbVersion").GetString().Should().Be(DateTimeZoneProviders.Tzdb.VersionId);
	}

	[Fact]
	public async Task A_cost_viewer_can_get_hierarchy_totals_via_the_api()
	{
		var (workerId, leafId) = await SeedWorkedLeafWithFinishedSessionAsync("cost.hierarchy.worker");
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "cost.hierarchy.viewer", EmployeeRole.CostViewer);
		var authCookie = await client.SignInAsync("cost.hierarchy.viewer");

		var response = await client.GetAuthenticatedAsync($"/api/jobs/{rootId.Value}/cost/hierarchy?asOf=2026-01-02T00:00:00%2B00:00", authCookie);
		var jsonDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		jsonDocument.RootElement.GetProperty("nodeId").GetInt64().Should().Be(rootId.Value);
		var nodes = jsonDocument.RootElement.GetProperty("nodes");
		nodes.EnumerateArray().Should().Contain(node => node.GetProperty("nodeId").GetInt64() == leafId.Value);
		nodes.EnumerateArray()
			 .Single(node => node.GetProperty("nodeId").GetInt64() == leafId.Value)
			 .GetProperty("allocatedHours").GetDecimal().Should().Be(8m);
		jsonDocument.RootElement.GetProperty("tzdbVersion").GetString().Should().Be(DateTimeZoneProviders.Tzdb.VersionId);
	}

	[Fact]
	public async Task A_worker_without_cost_permission_is_denied_and_receives_problem_details()
	{
		var (workerId, leafId) = await SeedWorkedLeafWithFinishedSessionAsync("cost.denied.worker");
		var authCookie = await client.SignInAsync("cost.denied.worker");

		var response = await client.GetAuthenticatedAsync($"/api/jobs/{leafId.Value}/cost?asOf=2026-01-02T00:00:00%2B00:00", authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task A_worker_without_cost_permission_is_denied_via_a_bearer_token_without_leaking_rate_or_cost_data()
	{
		var (workerId, leafId) = await SeedWorkedLeafWithFinishedSessionAsync("cost.bearer-denied.worker");
		var issued = await seedClient.Tokens.IssueAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
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
		var viewerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "cost.bearer.viewer", EmployeeRole.CostViewer);
		var issued = await seedClient.Tokens.IssueAsync(new() {
			Context = new() {
				Actor = viewerId,
				CorrelationId = Guid.NewGuid(),
			},
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
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "cost.trace-limit.viewer", EmployeeRole.CostViewer);
		var authCookie = await client.SignInAsync("cost.trace-limit.viewer");

		var response = await client.GetAuthenticatedAsync(
			$"/api/jobs/{leafId.Value}/cost?asOf=2026-01-02T00:00:00%2B00:00&maxTraceSegments=0",
			authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	[Fact]
	public async Task Cost_details_reports_an_unprocessable_entity_when_no_rate_resolves()
	{
		var (_, leafId) = await SeedWorkedLeafWithFinishedSessionAsync("cost.no-rate.worker", false);
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "cost.no-rate.viewer", EmployeeRole.CostViewer);
		var authCookie = await client.SignInAsync("cost.no-rate.viewer");

		var response = await client.GetAuthenticatedAsync($"/api/jobs/{leafId.Value}/cost?asOf=2026-01-02T00:00:00%2B00:00", authCookie);

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
		_ = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, "cost.node-limit.viewer", EmployeeRole.CostViewer);
		var authCookie = await client.SignInAsync("cost.node-limit.viewer");

		var response = await client.GetAuthenticatedAsync(
			$"/api/jobs/{rootId.Value}/cost/hierarchy?asOf=2026-01-02T00:00:00%2B00:00&maxHierarchyNodes=1",
			authCookie);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
	}

	private async Task<(AppUserId WorkerId, JobNodeId LeafId)> SeedWorkedLeafWithFinishedSessionAsync(
		string workerUserName, bool addUserRate = true)
	{
		var workerId = await IdentityTestSupport.SeedSqliteEmployeeAsync(database.ConnectionString, KnownPassword, workerUserName);
		var leaf = await seedClient.Jobs.AddChildAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			ParentId = rootId,
			Description = "Fit cabinets",
			OwnerUserId = workerId,
			Priority = Priority.Medium,
		});
		_ = await seedClient.Jobs.AttachLeafWorkAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			JobNodeId = leaf.Id,
		});
		_ = await seedClient.Schedules.AddScheduleExceptionAsync(new() {
			Context = new() {
				Actor = administratorId,
				CorrelationId = Guid.NewGuid(),
			},
			UserId = workerId,
			Entry = new(
				ScheduleExceptionEffect.AddWorkingTime,
				new(Instant.FromUtc(2026, 1, 1, 9, 0), Instant.FromUtc(2026, 1, 1, 18, 0)),
				null),
			Reason = "Full working window for cost-report API tests",
		});
		if (addUserRate) {
			_ = await seedClient.Rates.AddUserCostRateAsync(new() {
				Context = new() {
					Actor = administratorId,
					CorrelationId = Guid.NewGuid(),
				},
				UserId = workerId,
				Rate = new(new(25m), Instant.FromUtc(2026, 1, 1, 0, 0), null),
			});
		}

		var started = await seedClient.Work.StartSessionAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			LeafWorkId = leaf.Id,
			WorkedByUserId = workerId,
			StartedAt = Instant.FromUtc(2026, 1, 1, 9, 0),
		});
		_ = await seedClient.Work.FinishSessionAsync(new() {
			Context = new() {
				Actor = workerId,
				CorrelationId = Guid.NewGuid(),
			},
			SessionId = started.Id,
			Version = started.Version,
			FinishedAt = Instant.FromUtc(2026, 1, 1, 17, 0),
		});

		return (workerId, leaf.Id);
	}



	private async Task<HttpResponseMessage> GetWithBearerAsync(string path, string token)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.Authorization = new("Bearer", token);
		return await client.SendAsync(request);
	}







	[GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
	private static partial Regex AntiforgeryTokenPattern();
}
